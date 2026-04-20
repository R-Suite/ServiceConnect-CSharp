#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
RUN_ID=$(date +%s%N)
REQUESTER_QUEUE_NAME="scatter-gather-requester-${RUN_ID}"
CATALOG_A_QUEUE_NAME="scatter-gather-catalog-a-${RUN_ID}"
CATALOG_B_QUEUE_NAME="scatter-gather-catalog-b-${RUN_ID}"
SEARCH_QUERY="service-bus-${RUN_ID}"
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
    if grep -q '^READY:scatter-gather-catalog-a$' "$OUTPUT_LOG" 2>/dev/null &&
      grep -q '^READY:scatter-gather-catalog-b$' "$OUTPUT_LOG" 2>/dev/null; then
      return 0
    fi

    sleep 0.5
    attempt=$((attempt + 1))
  done

  return 1
}

verification_complete() {
  grep -q '^SUCCESS:scatter-gather-requester:received 2 replies$' "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:scatter-gather-catalog-a:returned CatalogA/catalog-a-result-001 for ${SEARCH_QUERY}$" "$OUTPUT_LOG" 2>/dev/null &&
    grep -q "^SUCCESS:scatter-gather-catalog-b:returned CatalogB/catalog-b-result-777 for ${SEARCH_QUERY}$" "$OUTPUT_LOG" 2>/dev/null
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

start_passive() {
  local project_path="$1"
  SC_EXAMPLES_CATALOG_A_QUEUE_NAME="$CATALOG_A_QUEUE_NAME" \
    SC_EXAMPLES_CATALOG_B_QUEUE_NAME="$CATALOG_B_QUEUE_NAME" \
    dotnet run --project "$project_path" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
> "$OUTPUT_LOG"

start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.ScatterGather.CatalogA/ServiceConnect.Examples.ScatterGather.CatalogA.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.ScatterGather.CatalogB/ServiceConnect.Examples.ScatterGather.CatalogB.csproj"

if ! wait_for_ready; then
  echo "ERROR: Catalog services did not become ready within 30 seconds"
  exit 1
fi

SC_EXAMPLES_REQUESTER_QUEUE_NAME="$REQUESTER_QUEUE_NAME" \
  SC_EXAMPLES_CATALOG_A_QUEUE_NAME="$CATALOG_A_QUEUE_NAME" \
  SC_EXAMPLES_CATALOG_B_QUEUE_NAME="$CATALOG_B_QUEUE_NAME" \
  SC_EXAMPLES_SEARCH_QUERY="$SEARCH_QUERY" \
  dotnet run --project "$SCRIPT_DIR/src/ServiceConnect.Examples.ScatterGather.Requester/ServiceConnect.Examples.ScatterGather.Requester.csproj" >> "$OUTPUT_LOG" 2>&1 &
REQUESTER_PID=$!
PIDS+=("$REQUESTER_PID")
if ! wait "$REQUESTER_PID"; then
  echo "ERROR: Requester exited before the scatter/gather flow completed"
  exit 1
fi

if ! wait_for_completion; then
  echo "ERROR: Scatter/gather run did not produce both catalog replies within 30 seconds"
  exit 1
fi
