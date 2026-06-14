#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

PIDS=()

cleanup() {
  for pid in "${PIDS[@]:-}"; do
    kill "$pid" || true
  done

  for pid in "${PIDS[@]:-}"; do
    wait "$pid" || true
  done
}

trap cleanup EXIT

start_passive() {
  dotnet run --no-build --project "$1" &
  PIDS+=("$!")
}

start_dependencies
docker compose -f "$SCRIPT_DIR/../docker-compose.yml" exec -T rabbitmq rabbitmqctl purge_queue point-to-point-consumer || true
prebuild_solution "$SCRIPT_DIR/PointToPoint.sln"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.PointToPoint.Consumer/ServiceConnect.Examples.PointToPoint.Consumer.csproj"
sleep 5
dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.PointToPoint.Sender/ServiceConnect.Examples.PointToPoint.Sender.csproj"
sleep 5
