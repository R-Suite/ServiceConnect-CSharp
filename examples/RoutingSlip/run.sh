#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
ORDER_ID="routing-slip-order-${RUN_ID}"
INVENTORY_QUEUE_NAME="routing-slip-inventory-${RUN_ID}"
BILLING_QUEUE_NAME="routing-slip-billing-${RUN_ID}"
SHIPPING_QUEUE_NAME="routing-slip-shipping-${RUN_ID}"
PIDS=()

cleanup() {
  for pid in "${PIDS[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done

  for pid in "${PIDS[@]:-}"; do
    wait "$pid" 2>/dev/null || true
  done
}

trap cleanup EXIT

wait_for_ready() {
  local max_attempts=60
  local attempt=0

  while [ $attempt -lt $max_attempts ]; do
    if grep -q '^READY:inventory-step$' "$OUTPUT_LOG" 2>/dev/null &&
      grep -q '^READY:billing-step$' "$OUTPUT_LOG" 2>/dev/null &&
      grep -q '^READY:shipping-step$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

verification_complete() {
  grep -q "^SUCCESS:routing-slip-starter:routed ${ORDER_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:inventory-step:processed ${ORDER_ID} at InventoryStep$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:billing-step:processed ${ORDER_ID} at BillingStep$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:shipping-step:processed ${ORDER_ID} at ShippingStep$" "$OUTPUT_LOG" 2>/dev/null
}

wait_for_completion() {
  local max_attempts=60
  local attempt=0

  while [ $attempt -lt $max_attempts ]; do
    if verification_complete; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

start_passive() {
  local project_path="$1"
  SC_EXAMPLES_INVENTORY_QUEUE_NAME="$INVENTORY_QUEUE_NAME" \
    SC_EXAMPLES_BILLING_QUEUE_NAME="$BILLING_QUEUE_NAME" \
    SC_EXAMPLES_SHIPPING_QUEUE_NAME="$SHIPPING_QUEUE_NAME" \
    dotnet run --project "$project_path" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
> "$OUTPUT_LOG"

start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.RoutingSlip.InventoryStep/ServiceConnect.Examples.RoutingSlip.InventoryStep.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.RoutingSlip.BillingStep/ServiceConnect.Examples.RoutingSlip.BillingStep.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.RoutingSlip.ShippingStep/ServiceConnect.Examples.RoutingSlip.ShippingStep.csproj"

if ! wait_for_ready; then
  echo "ERROR: Step consumers did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_INVENTORY_QUEUE_NAME="$INVENTORY_QUEUE_NAME" \
  SC_EXAMPLES_BILLING_QUEUE_NAME="$BILLING_QUEUE_NAME" \
  SC_EXAMPLES_SHIPPING_QUEUE_NAME="$SHIPPING_QUEUE_NAME" \
  SC_EXAMPLES_ORDER_ID="$ORDER_ID" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.RoutingSlip.Starter/ServiceConnect.Examples.RoutingSlip.Starter.csproj" >> "$OUTPUT_LOG" 2>&1 &
STARTER_PID=$!
PIDS+=("$STARTER_PID")
wait "$STARTER_PID"

if ! wait_for_completion; then
  echo "ERROR: Routing slip did not complete all three steps within 30 seconds"
  exit 1
fi
