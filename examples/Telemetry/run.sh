#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../scripts/common.sh"

OUTPUT_LOG="$SCRIPT_DIR/output.log"
PIDS=()

wait_for_ready() {
  local timeout=30

  for i in $(seq 1 $((timeout * 2))); do
    if grep -q "READY:billing-subscriber" "$OUTPUT_LOG" && grep -q "READY:analytics-subscriber" "$OUTPUT_LOG"; then
      return 0
    fi
    sleep 0.5
  done

  return 1
}

wait_for_success() {
  local timeout=30

  # Wait for the handlers to print AND for the consume Activity to be Stop()'d
  # (which happens AFTER the handler returns, so it's strictly later than the
  # BILLING:/ANALYTICS:received: lines). Without this, the assertion block can
  # race the consume-side TRACE: lines and kill the subscribers before they
  # flush, leaving them missing from the log.
  for i in $(seq 1 $((timeout * 2))); do
    if grep -q "SUCCESS:telemetry-publisher:published order" "$OUTPUT_LOG" &&
      grep -q "BILLING:received:" "$OUTPUT_LOG" &&
      grep -q "ANALYTICS:received:" "$OUTPUT_LOG" &&
      grep -qE '^TRACE:billing-subscriber:[A-Za-z.]+:' "$OUTPUT_LOG" &&
      grep -qE '^TRACE:analytics-subscriber:[A-Za-z.]+:' "$OUTPUT_LOG"; then
      return 0
    fi
    sleep 0.5
  done

  return 1
}

start_passive() {
  dotnet run --no-build --project "$1" >> "$OUTPUT_LOG" 2>&1 &
  PIDS+=("$!")
}

start_dependencies
> "$OUTPUT_LOG"

# Pre-build all three projects sequentially. Two parallel `dotnet run` invocations
# each trigger an implicit restore + build that contend on the shared output
# directory and exhaust the build cgroup memory. Building once up front lets the
# subsequent `dotnet run --no-build` calls just exec the cached binaries.
for proj in \
  "$SCRIPT_DIR/src/ServiceConnect.Examples.Telemetry.BillingSubscriber/ServiceConnect.Examples.Telemetry.BillingSubscriber.csproj" \
  "$SCRIPT_DIR/src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber.csproj" \
  "$SCRIPT_DIR/src/ServiceConnect.Examples.Telemetry.Publisher/ServiceConnect.Examples.Telemetry.Publisher.csproj"; do
  dotnet build "$proj" -m:1 --nologo --verbosity quiet >> "$OUTPUT_LOG" 2>&1
done

start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.Telemetry.BillingSubscriber/ServiceConnect.Examples.Telemetry.BillingSubscriber.csproj"
start_passive "$SCRIPT_DIR/src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber.csproj"

if ! wait_for_ready; then
  echo "ERROR: Subscribers did not become ready within 30 seconds"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

dotnet run --no-build --project "$SCRIPT_DIR/src/ServiceConnect.Examples.Telemetry.Publisher/ServiceConnect.Examples.Telemetry.Publisher.csproj" >> "$OUTPUT_LOG" 2>&1 &
PUBLISHER_PID=$!
PIDS+=("$PUBLISHER_PID")
wait "$PUBLISHER_PID"

if ! wait_for_success; then
  echo "ERROR: Subscribers did not both receive the order within 30 seconds"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

# Trace-correlation assertions ----------------------------------------
PUB_LINE=$(grep -E '^TRACE:telemetry-publisher:[A-Za-z.]+:' "$OUTPUT_LOG" | head -n1)
BILL_LINE=$(grep -E '^TRACE:billing-subscriber:[A-Za-z.]+:' "$OUTPUT_LOG" | head -n1)
ANALYTICS_LINE=$(grep -E '^TRACE:analytics-subscriber:[A-Za-z.]+:' "$OUTPUT_LOG" | head -n1)

if [ -z "$PUB_LINE" ] || [ -z "$BILL_LINE" ] || [ -z "$ANALYTICS_LINE" ]; then
  echo "FAIL: missing TRACE: line for one or more processes" >&2
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi

# TRACE:<endpoint>:<op>:<trace>:<span>:<parent>
PUB_TRACE=$(echo "$PUB_LINE" | awk -F: '{print $4}')
PUB_SPAN=$(echo "$PUB_LINE" | awk -F: '{print $5}')
BILL_TRACE=$(echo "$BILL_LINE" | awk -F: '{print $4}')
BILL_PARENT=$(echo "$BILL_LINE" | awk -F: '{print $6}')
ANALYTICS_TRACE=$(echo "$ANALYTICS_LINE" | awk -F: '{print $4}')
ANALYTICS_PARENT=$(echo "$ANALYTICS_LINE" | awk -F: '{print $6}')

if [ "$PUB_TRACE" != "$BILL_TRACE" ] || [ "$PUB_TRACE" != "$ANALYTICS_TRACE" ]; then
  echo "FAIL: TraceId mismatch (pub=$PUB_TRACE bill=$BILL_TRACE analytics=$ANALYTICS_TRACE)" >&2
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi
if [ "$BILL_PARENT" != "$PUB_SPAN" ] || [ "$ANALYTICS_PARENT" != "$PUB_SPAN" ]; then
  echo "FAIL: ParentSpanId mismatch (pub=$PUB_SPAN bill=$BILL_PARENT analytics=$ANALYTICS_PARENT)" >&2
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  exit 1
fi
echo "OK: trace-id correlated across publisher and both subscribers"

for pid in "${PIDS[@]}"; do
  kill "$pid" 2>/dev/null || true
done
for pid in "${PIDS[@]}"; do
  wait "$pid" 2>/dev/null || true
done
