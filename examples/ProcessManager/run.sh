#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
WORKFLOW_QUEUE_NAME="process-manager-orchestrator-${RUN_ID}"
INVENTORY_QUEUE_NAME="process-manager-inventory-${RUN_ID}"
PAYMENT_QUEUE_NAME="process-manager-payment-${RUN_ID}"
DATABASE_NAME="process_manager_${RUN_ID}"
CORRELATION_ID=$(cat /proc/sys/kernel/random/uuid)
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
    if grep -q '^READY:process-manager-orchestrator$' "$OUTPUT_LOG" 2>/dev/null &&
      grep -q '^READY:inventory-worker$' "$OUTPUT_LOG" 2>/dev/null &&
      grep -q '^READY:payment-worker$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

verification_complete() {
  grep -q "^SUCCESS:process-manager-starter:submitted ${CORRELATION_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:process-manager-orchestrator:started workflow ${CORRELATION_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:inventory-worker:reserved inventory for ${CORRELATION_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:process-manager-orchestrator:inventory reserved for ${CORRELATION_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:payment-worker:captured payment for ${CORRELATION_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:process-manager-orchestrator:completed workflow ${CORRELATION_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    docker compose -f "$SCRIPT_DIR/../docker-compose.yml" exec -T mongodb mongosh --quiet "$DATABASE_NAME" --eval "const doc = db['ServiceConnect.Examples.ProcessManager.Contracts.FulfillmentState'].findOne({ 'Data.CorrelationId': UUID('${CORRELATION_ID}') }); if (!doc || !doc.Data.IsCompleted || !doc.Data.InventoryReserved || !doc.Data.PaymentCaptured) { quit(1); }" >/dev/null 2>&1
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

start_dependencies
prebuild_solution "$SCRIPT_DIR/ProcessManager.sln"
> "$OUTPUT_LOG"

SC_EXAMPLES_WORKFLOW_QUEUE_NAME="$WORKFLOW_QUEUE_NAME" \
  SC_EXAMPLES_INVENTORY_QUEUE_NAME="$INVENTORY_QUEUE_NAME" \
  SC_EXAMPLES_PAYMENT_QUEUE_NAME="$PAYMENT_QUEUE_NAME" \
  SC_EXAMPLES_DATABASE_NAME="$DATABASE_NAME" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.ProcessManager.Orchestrator/ServiceConnect.Examples.ProcessManager.Orchestrator.csproj" >> "$OUTPUT_LOG" 2>&1 &
ORCHESTRATOR_PID=$!
PIDS+=("$ORCHESTRATOR_PID")

SC_EXAMPLES_WORKFLOW_QUEUE_NAME="$WORKFLOW_QUEUE_NAME" \
  SC_EXAMPLES_INVENTORY_QUEUE_NAME="$INVENTORY_QUEUE_NAME" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.ProcessManager.InventoryWorker/ServiceConnect.Examples.ProcessManager.InventoryWorker.csproj" >> "$OUTPUT_LOG" 2>&1 &
INVENTORY_PID=$!
PIDS+=("$INVENTORY_PID")

SC_EXAMPLES_WORKFLOW_QUEUE_NAME="$WORKFLOW_QUEUE_NAME" \
  SC_EXAMPLES_PAYMENT_QUEUE_NAME="$PAYMENT_QUEUE_NAME" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.ProcessManager.PaymentWorker/ServiceConnect.Examples.ProcessManager.PaymentWorker.csproj" >> "$OUTPUT_LOG" 2>&1 &
PAYMENT_PID=$!
PIDS+=("$PAYMENT_PID")

if ! wait_for_ready; then
  echo "ERROR: Process manager services did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_WORKFLOW_QUEUE_NAME="$WORKFLOW_QUEUE_NAME" \
  SC_EXAMPLES_CORRELATION_ID="$CORRELATION_ID" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.ProcessManager.Starter/ServiceConnect.Examples.ProcessManager.Starter.csproj" >> "$OUTPUT_LOG" 2>&1 &
STARTER_PID=$!
PIDS+=("$STARTER_PID")
wait "$STARTER_PID"

if ! wait_for_completion; then
  echo "ERROR: Process manager workflow did not complete within 30 seconds"
  exit 1
fi
