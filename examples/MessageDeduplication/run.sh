#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
QUEUE_NAME="dedup-consumer-${RUN_ID}"
MONGO_DB="dedup-sample-${RUN_ID}"
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
    if grep -q '^READY:dedup-consumer$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

wait_for_blocked_redelivery() {
  local max_attempts=40
  local attempt=0

  while [ $attempt -lt $max_attempts ]; do
    local handled_count
    handled_count=$(grep -c 'SUCCESS:dedup-consumer:handled' "$OUTPUT_LOG" 2>/dev/null) || handled_count=0
    if [ "$handled_count" -ge 1 ]; then
      sleep 2
      local final_count
      final_count=$(grep -c 'SUCCESS:dedup-consumer:handled' "$OUTPUT_LOG" 2>/dev/null) || final_count=0
      if [ "$final_count" -eq 1 ]; then
        return 0
      fi
      echo "FAIL: handler ran $final_count times — expected 1 (filter did not block redelivery)"
      return 1
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  echo "FAIL: handler never invoked within 20s"
  return 1
}

start_dependencies
> "$OUTPUT_LOG"

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  SC_DEDUP_MONGO_DB="$MONGO_DB" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.MessageDeduplication.Consumer/ServiceConnect.Examples.MessageDeduplication.Consumer.csproj" >> "$OUTPUT_LOG" 2>&1 &
CONSUMER_PID=$!
PIDS+=("$CONSUMER_PID")

if ! wait_for_ready; then
  echo "ERROR: Consumer did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_QUEUE_NAME="dedup-sender-${RUN_ID}" \
  SC_EXAMPLES_CONSUMER_QUEUE="$QUEUE_NAME" \
  SC_DEDUP_MONGO_DB="$MONGO_DB" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.MessageDeduplication.Sender/ServiceConnect.Examples.MessageDeduplication.Sender.csproj" >> "$OUTPUT_LOG" 2>&1

if ! wait_for_blocked_redelivery; then
  exit 1
fi

echo "SUCCESS: handler invoked exactly once; filter blocked the redelivery."
