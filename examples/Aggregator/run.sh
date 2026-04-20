#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
QUEUE_NAME="aggregator-consumer-${RUN_ID}"
DATABASE_NAME="aggregator_consumer_${RUN_ID}"
CORRELATION_ID=$(printf '%08x-%04x-%04x-%04x-%012x' $((RANDOM << 16 | RANDOM)) $((RANDOM)) $((RANDOM)) $((RANDOM)) $(( (RANDOM << 16 | RANDOM) << 16 | RANDOM )) )
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
    if grep -q '^READY:aggregator-consumer$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

verification_complete() {
  grep -q '^SUCCESS:aggregator-producer-a:sent ProducerA/10$' "$OUTPUT_LOG" 2>/dev/null &&
    grep -q '^SUCCESS:aggregator-producer-b:sent ProducerB/15$' "$OUTPUT_LOG" 2>/dev/null &&
    grep -q '^SUCCESS:aggregator-consumer:combined total 25 from 2 slices$' "$OUTPUT_LOG" 2>/dev/null
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
> "$OUTPUT_LOG"

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  SC_EXAMPLES_DATABASE_NAME="$DATABASE_NAME" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Aggregator.Consumer/ServiceConnect.Examples.Aggregator.Consumer.csproj" >> "$OUTPUT_LOG" 2>&1 &
CONSUMER_PID=$!
PIDS+=("$CONSUMER_PID")

if ! wait_for_ready; then
  echo "ERROR: Aggregator consumer did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  SC_EXAMPLES_CORRELATION_ID="$CORRELATION_ID" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Aggregator.ProducerA/ServiceConnect.Examples.Aggregator.ProducerA.csproj" >> "$OUTPUT_LOG" 2>&1 &
PRODUCER_A_PID=$!
PIDS+=("$PRODUCER_A_PID")

SC_EXAMPLES_QUEUE_NAME="$QUEUE_NAME" \
  SC_EXAMPLES_CORRELATION_ID="$CORRELATION_ID" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Aggregator.ProducerB/ServiceConnect.Examples.Aggregator.ProducerB.csproj" >> "$OUTPUT_LOG" 2>&1 &
PRODUCER_B_PID=$!
PIDS+=("$PRODUCER_B_PID")

wait "$PRODUCER_A_PID"
wait "$PRODUCER_B_PID"

if ! wait_for_completion; then
  echo "ERROR: Aggregator run did not produce the combined total within 30 seconds"
  exit 1
fi
