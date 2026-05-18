#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
PIDS=()

wait_for_ready() {
  local timeout=30

  for i in $(seq 1 $((timeout * 2))); do
    if grep -q "READY:billing-subscriber" "$OUTPUT_LOG" && grep -q "READY:analytics-subscriber" "$OUTPUT_LOG"; then
      return 0
    fi
    sleep 0.5
  done

  return 1
}

wait_for_success() {
  local timeout=30

  for i in $(seq 1 $((timeout * 2))); do
    if grep -q "SUCCESS:billing-subscriber:processed order-100" "$OUTPUT_LOG" &&
      grep -q "SUCCESS:analytics-subscriber:processed order-100" "$OUTPUT_LOG"; then
      return 0
    fi
    sleep 0.5
  done

  return 1
}

start_passive() {
  dotnet run --no-build --project "$1" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
prebuild_solution "$SCRIPT_DIR/PublishSubscribe.sln"
> "$OUTPUT_LOG"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.PublishSubscribe.BillingSubscriber/ServiceConnect.Examples.PublishSubscribe.BillingSubscriber.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.PublishSubscribe.AnalyticsSubscriber/ServiceConnect.Examples.PublishSubscribe.AnalyticsSubscriber.csproj"

if ! wait_for_ready; then
  echo "ERROR: Subscribers did not become ready within 30 seconds"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.PublishSubscribe.Publisher/ServiceConnect.Examples.PublishSubscribe.Publisher.csproj" &
PUBLISHER_PID=$!
PIDS+=("$PUBLISHER_PID")
wait "$PUBLISHER_PID"

if ! wait_for_success; then
  echo "ERROR: Subscribers did not both process order-100 within 30 seconds"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

for pid in "${PIDS[@]}"; do
  kill "$pid" 2>/dev/null || true
done
for pid in "${PIDS[@]}"; do
  wait "$pid" 2>/dev/null || true
done
