#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
PIDS=()

wait_for_ready() {
  local timeout=30

  for i in $(seq 1 $((timeout * 2))); do
    if grep -q "READY:request-reply-responder" "$OUTPUT_LOG"; then
      return 0
    fi
    sleep 0.5
  done

  return 1
}

start_passive() {
  dotnet run --project "$1" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
> "$OUTPUT_LOG"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.RequestReply.Responder/ServiceConnect.Examples.RequestReply.Responder.csproj"

if ! wait_for_ready; then
  echo "ERROR: Responder did not become ready within 30 seconds"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.RequestReply.Requester/ServiceConnect.Examples.RequestReply.Requester.csproj" >> "$OUTPUT_LOG" 2>&1 &
REQUESTER_PID=$!
PIDS+=("$REQUESTER_PID")
wait "$REQUESTER_PID"

for pid in "${PIDS[@]}"; do
  kill "$pid" 2>/dev/null || true
done
for pid in "${PIDS[@]}"; do
  wait "$pid" 2>/dev/null || true
done