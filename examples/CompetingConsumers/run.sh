#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
QUEUE_NAME="competing-consumers-queue-${RUN_ID}"
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
    if grep -q "READY:worker-a" "$OUTPUT_LOG" 2>/dev/null && grep -q "READY:worker-b" "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi
    sleep 0.5
    attempt=$((attempt + 1))
  done
  return 1
}

completion_condition_met() {
  local -a lines
  mapfile -t lines < <(grep -E '^SUCCESS:worker-(a|b):processed job-[0-9]{3}$' "$OUTPUT_LOG" 2>/dev/null || true)

  if [ "${#lines[@]}" -ne 10 ]; then
    return 1
  fi

  local unique_jobs
  unique_jobs=$(printf '%s\n' "${lines[@]}" | sed -E 's/^SUCCESS:worker-(a|b):processed //' | sort -u | wc -l | tr -d ' ')
  [ "$unique_jobs" -eq 10 ]
}

wait_for_completion() {
  local max_attempts=60
  local attempt=0

  while [ $attempt -lt $max_attempts ]; do
    if completion_condition_met; then
      return 0
    fi
    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

start_passive() {
  local project_path="$1"
  SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" dotnet run --project "$project_path" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
> "$OUTPUT_LOG"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.CompetingConsumers.WorkerA/ServiceConnect.Examples.CompetingConsumers.WorkerA.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.CompetingConsumers.WorkerB/ServiceConnect.Examples.CompetingConsumers.WorkerB.csproj"

if ! wait_for_ready; then
  echo "ERROR: Workers did not become ready within 30 seconds"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.CompetingConsumers.Producer/ServiceConnect.Examples.CompetingConsumers.Producer.csproj" >> "$OUTPUT_LOG" 2>&1 &
PUBLISHER_PID=$!
wait "$PUBLISHER_PID"

if ! wait_for_completion; then
  echo "ERROR: Workers did not process all 10 jobs within 30 seconds"
  exit 1
fi

exit 0
