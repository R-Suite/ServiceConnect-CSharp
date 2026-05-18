#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
PREMIUM_ORDER_ID="premium-order-${RUN_ID}"
STANDARD_ORDER_ID="standard-order-${RUN_ID}"
PRIORITY_QUEUE_NAME="priority-consumer-${RUN_ID}"
STANDARD_QUEUE_NAME="standard-consumer-${RUN_ID}"
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
    if grep -q '^READY:priority-consumer$' "$OUTPUT_LOG" 2>/dev/null && grep -q '^READY:standard-consumer$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

verification_complete() {
  grep -q "^SUCCESS:content-based-routing-publisher:published ${PREMIUM_ORDER_ID} and ${STANDARD_ORDER_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:priority-consumer:processed ${PREMIUM_ORDER_ID}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:standard-consumer:processed ${STANDARD_ORDER_ID}$" "$OUTPUT_LOG" 2>/dev/null
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
  SC_EXAMPLES_PRIORITY_QUEUE_NAME="$PRIORITY_QUEUE_NAME" \
    SC_EXAMPLES_STANDARD_QUEUE_NAME="$STANDARD_QUEUE_NAME" \
    dotnet run --no-build --project "$project_path" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
prebuild_solution "$SCRIPT_DIR/ContentBasedRouting.sln"
> "$OUTPUT_LOG"

start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.ContentBasedRouting.PriorityConsumer/ServiceConnect.Examples.ContentBasedRouting.PriorityConsumer.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.ContentBasedRouting.StandardConsumer/ServiceConnect.Examples.ContentBasedRouting.StandardConsumer.csproj"

if ! wait_for_ready; then
  echo "ERROR: Consumers did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_PREMIUM_ORDER_ID="$PREMIUM_ORDER_ID" \
  SC_EXAMPLES_STANDARD_ORDER_ID="$STANDARD_ORDER_ID" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.ContentBasedRouting.Publisher/ServiceConnect.Examples.ContentBasedRouting.Publisher.csproj" >> "$OUTPUT_LOG" 2>&1 &
PUBLISHER_PID=$!
PIDS+=("$PUBLISHER_PID")
wait "$PUBLISHER_PID"

if ! wait_for_completion; then
  echo "ERROR: Consumers did not process the expected routed events within 30 seconds"
  exit 1
fi

if grep -Eq '^SUCCESS:priority-consumer:processed standard-order-' "$OUTPUT_LOG" 2>/dev/null; then
  echo "ERROR: Priority consumer processed a standard order"
  exit 1
fi

if grep -Eq '^SUCCESS:standard-consumer:processed premium-order-' "$OUTPUT_LOG" 2>/dev/null; then
  echo "ERROR: Standard consumer processed a premium order"
  exit 1
fi
