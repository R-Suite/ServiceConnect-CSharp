#!/usr/bin/env bash
set -euo pipefail

EXAMPLES_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

start_dependencies() {
  docker compose -f "$EXAMPLES_ROOT/docker-compose.yml" up -d rabbitmq mongodb
}
