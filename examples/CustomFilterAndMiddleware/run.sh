#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
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
    if grep -q '^READY:custom-filter-consumer$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

start_dependencies
prebuild_solution "$SCRIPT_DIR/CustomFilterAndMiddleware.slnx"
> "$OUTPUT_LOG"

dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj" >> "$OUTPUT_LOG" 2>&1 &
CONSUMER_PID=$!
PIDS+=("$CONSUMER_PID")

if ! wait_for_ready; then
  echo "ERROR: Consumer did not become ready within 30 seconds"
  exit 1
fi

dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj" >> "$OUTPUT_LOG" 2>&1

sleep 5

kill "$CONSUMER_PID" 2>/dev/null || true
wait "$CONSUMER_PID" 2>/dev/null || true

echo "--- Consumer log ---"
cat "$OUTPUT_LOG"
