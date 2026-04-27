#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
QUEUE_NAME="filters-consumer-${RUN_ID}"
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
    if grep -q '^READY:filters-consumer$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

wait_for_success() {
  local max_attempts=60
  local attempt=0

  while [ $attempt -lt $max_attempts ]; do
    if grep -q '^SUCCESS:filters-consumer:trace trace-001$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

start_dependencies
> "$OUTPUT_LOG"

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Filters.Consumer/ServiceConnect.Examples.Filters.Consumer.csproj" >> "$OUTPUT_LOG" 2>&1 &
CONSUMER_PID=$!
PIDS+=("$CONSUMER_PID")

if ! wait_for_ready; then
  echo "ERROR: Filters consumer did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Filters.Sender/ServiceConnect.Examples.Filters.Sender.csproj" >> "$OUTPUT_LOG" 2>&1

if ! wait_for_success; then
  echo "ERROR: Filters run did not produce the expected consumer success line within 30 seconds"
  exit 1
fi
