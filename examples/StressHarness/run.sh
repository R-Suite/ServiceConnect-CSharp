#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# shellcheck source=../scripts/common.sh
source "../scripts/common.sh"

MODE="${MODE:-smoke}"
DURATION="${DURATION:-5m}"
RATE="${RATE:-100}"
PERSISTENCE="${PERSISTENCE:-inmemory}"
CHAOS="${CHAOS:-none}"

trap 'docker compose -p stress-harness down --remove-orphans >/dev/null 2>&1 || true' EXIT

docker compose -p stress-harness up -d
wait_for_rabbit "localhost" "5672"
if [ "$PERSISTENCE" = "mongo" ]; then
  wait_for_mongo "localhost" "27017"
fi

dotnet run \
  --project src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj \
  -- \
  --mode "$MODE" \
  --duration "$DURATION" \
  --rate "$RATE" \
  --persistence "$PERSISTENCE" \
  --chaos "$CHAOS"
