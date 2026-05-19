#!/usr/bin/env bash
set -euo pipefail

EXAMPLES_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

start_dependencies() {
  docker compose -f "$EXAMPLES_ROOT/docker-compose.yml" up -d rabbitmq mongodb
}

# Sequentially builds the example's solution under -m:1 so subsequent
# `dotnet run --no-build` calls become lightweight process-spawn + JIT
# rather than each one triggering its own analyzer-heavy compile. The
# parallel-compile pattern previously hit the dotnet-build.slice cgroup's
# 200-task / 8 G ceiling (MSBuild Copy task OOM, MA0049-style cascade).
# Single argument: absolute path to the .sln (or .slnx).
# Polls until RabbitMQ accepts a TCP connection on the given host/port.
# Usage: wait_for_rabbit <host> <port>
wait_for_rabbit() {
  local host="$1"
  local port="$2"
  local max_attempts=60
  local attempt=0
  echo "Waiting for RabbitMQ at $host:$port..."
  while [ $attempt -lt $max_attempts ]; do
    if nc -z "$host" "$port" 2>/dev/null; then
      echo "RabbitMQ is ready."
      return 0
    fi
    sleep 2
    attempt=$((attempt + 1))
  done
  echo "ERROR: RabbitMQ at $host:$port did not become ready within $((max_attempts * 2)) seconds." >&2
  return 1
}

# Polls until MongoDB responds to an admin ping on the given host/port.
# Usage: wait_for_mongo <host> <port>
wait_for_mongo() {
  local host="$1"
  local port="$2"
  local max_attempts=60
  local attempt=0
  echo "Waiting for MongoDB at $host:$port..."
  while [ $attempt -lt $max_attempts ]; do
    if mongosh --host "$host" --port "$port" --quiet --eval "db.adminCommand('ping')" >/dev/null 2>&1; then
      echo "MongoDB is ready."
      return 0
    fi
    sleep 2
    attempt=$((attempt + 1))
  done
  echo "ERROR: MongoDB at $host:$port did not become ready within $((max_attempts * 2)) seconds." >&2
  return 1
}

prebuild_solution() {
    local solution_path="$1"
    if [ ! -f "$solution_path" ]; then
        echo "prebuild_solution: solution not found: $solution_path" >&2
        return 1
    fi
    echo "Pre-building $(basename "$solution_path") sequentially (-m:1)..."
    dotnet build "$solution_path" -m:1 --nologo --verbosity quiet
}
