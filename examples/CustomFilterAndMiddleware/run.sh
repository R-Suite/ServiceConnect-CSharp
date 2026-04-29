#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
PIDS=()

cleanup() {
  for pid in "${PIDS[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done

  for pid in "${PIDS[@]:-}"; do
    wait "$pid" 2>/dev/null || true
  done

  docker rm -f custom-filter-rabbit 2>/dev/null || true
}

trap cleanup EXIT

wait_for_rabbitmq() {
  local max_attempts=60
  local attempt=0

  while [ $attempt -lt $max_attempts ]; do
    # Use a TCP connection check rather than docker-exec + rabbitmq-diagnostics.
    # rabbitmq-diagnostics spawns an Erlang node on every call; doing that
    # rapidly during broker startup destabilises the EPMD and causes the
    # container to crash before the broker is ready.
    if nc -z localhost 5672 2>/dev/null; then
      return 0
    fi
    sleep 1
    attempt=$((attempt + 1))
  done

  echo "ERROR: RabbitMQ did not become ready within 60 seconds"
  return 1
}

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

docker run -d --rm --name custom-filter-rabbit -p 5672:5672 rabbitmq:3.13-management
wait_for_rabbitmq

> "$OUTPUT_LOG"

dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj" >> "$OUTPUT_LOG" 2>&1 &
CONSUMER_PID=$!
PIDS+=("$CONSUMER_PID")

if ! wait_for_ready; then
  echo "ERROR: Consumer did not become ready within 30 seconds"
  exit 1
fi

dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj" >> "$OUTPUT_LOG" 2>&1

sleep 5

kill "$CONSUMER_PID" 2>/dev/null || true
wait "$CONSUMER_PID" 2>/dev/null || true

echo "--- Consumer log ---"
cat "$OUTPUT_LOG"
