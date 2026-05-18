#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
QUEUE_NAME="streaming-receiver-${RUN_ID}"
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
    if grep -q '^READY:streaming-receiver$' "$OUTPUT_LOG" 2>/dev/null; then
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
    if grep -q '^SUCCESS:streaming-uploader:sent 3 chunks for demo-document.txt$' "$OUTPUT_LOG" 2>/dev/null &&
      grep -q '^SUCCESS:streaming-receiver:received demo-document.txt with 103 bytes$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

start_dependencies
prebuild_solution "$SCRIPT_DIR/Streaming.sln"
> "$OUTPUT_LOG"

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Streaming.Receiver/ServiceConnect.Examples.Streaming.Receiver.csproj" >> "$OUTPUT_LOG" 2>&1 &
RECEIVER_PID=$!
PIDS+=("$RECEIVER_PID")

if ! wait_for_ready; then
  echo "ERROR: Streaming receiver did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_ENDPOINT_NAME="$QUEUE_NAME" \
  dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Streaming.Uploader/ServiceConnect.Examples.Streaming.Uploader.csproj" >> "$OUTPUT_LOG" 2>&1

if ! wait_for_success; then
  echo "ERROR: Streaming run did not produce the expected success lines within 30 seconds"
  exit 1
fi
