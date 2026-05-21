#!/usr/bin/env bash
# Runs unit tests, E2E tests, a 5-minute harness soak (no chaos), and a 5-minute
# harness chaos soak. Exits non-zero on the first failure. Tears down the harness's
# Docker compose project on exit (including failure or interrupt).
#
# Designed for the project's cgroup-fenced dotnet wrapper at ~/.local/bin/dotnet:
# every dotnet build / test / run invocation uses -m:1, and the harness runs
# explicitly with --no-build after a single pre-build to avoid the parallel-
# restore + parallel-copy OOM that runs the wrapper into its 8 GB / 200-tasks
# ceiling.
#
# Usage:
#   ./verify-all.sh
#
# Optional environment overrides:
#   SKIP_UNIT=1         skip unit tests
#   SKIP_E2E=1          skip E2E tests
#   SKIP_HARNESS=1      skip the 5-minute soak
#   SKIP_CHAOS=1        skip the 5-minute chaos soak
#   HARNESS_DURATION    duration for both harness runs (default 00:05:00)
#   FLOW_TIMEOUT        per-flow assertion timeout (default 00:01:30 — covers
#                       framework retry budget under chaos)
#   CHAOS_INTERVAL      seconds between kill cycles (default 00:00:50)
#   CHAOS_DOWNTIME      broker downtime per cycle (default 00:00:20)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Defaults — overridable via env.
HARNESS_DURATION="${HARNESS_DURATION:-00:05:00}"
FLOW_TIMEOUT="${FLOW_TIMEOUT:-00:01:30}"
CHAOS_INTERVAL="${CHAOS_INTERVAL:-00:00:50}"
CHAOS_DOWNTIME="${CHAOS_DOWNTIME:-00:00:20}"

UNIT_PROJ="src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj"
E2E_PROJ="src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj"
HARNESS_PROJ="examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj"
COMPOSE_FILE="examples/StressHarness/docker-compose.yml"
COMPOSE_PROJECT="stress-harness"

REPORT_MD="out/report.md"

section() {
    printf '\n\033[1;36m==========================================================================\033[0m\n'
    printf '\033[1;36m  %s\033[0m\n' "$1"
    printf '\033[1;36m==========================================================================\033[0m\n\n'
}

fail() {
    printf '\n\033[1;31mFAILED:\033[0m %s\n' "$1" >&2
    exit 1
}

teardown_compose() {
    docker compose -f "$COMPOSE_FILE" -p "$COMPOSE_PROJECT" down --remove-orphans >/dev/null 2>&1 || true
}

trap teardown_compose EXIT INT TERM

bring_up_broker() {
    # --wait blocks until services with healthchecks defined in the compose file
    # report `healthy`. The rabbit service's healthcheck is `rabbitmqctl status`,
    # which only succeeds once the AMQP layer is fully initialised — stronger
    # than a TCP-port probe, which would unblock before connection.start can be
    # serviced.
    docker compose -f "$COMPOSE_FILE" -p "$COMPOSE_PROJECT" up -d --wait --wait-timeout 120 >/dev/null \
        || fail "broker did not reach healthy state within 120s"
}

verify_harness_passed() {
    local label="$1"
    [ -f "$REPORT_MD" ] || fail "$label: expected $REPORT_MD but it was not written"
    local flows_line
    flows_line=$(grep -E '^\*\*Flows:\*\*' "$REPORT_MD" | head -1)
    [ -n "$flows_line" ] || fail "$label: could not find Flows: line in $REPORT_MD"
    # Format: "**Flows:** PASSED / TOTAL passed". Failure if PASSED != TOTAL.
    local passed total
    passed=$(printf '%s' "$flows_line" | sed -E 's/.*\*\*Flows:\*\* +([0-9]+) +\/ +([0-9]+) +passed.*/\1/')
    total=$(printf '%s' "$flows_line" | sed -E 's/.*\*\*Flows:\*\* +([0-9]+) +\/ +([0-9]+) +passed.*/\2/')
    if [ "$passed" != "$total" ]; then
        printf '\n%s\n' "$flows_line"
        printf '\n--- assertion failures ---\n'
        awk '/## Assertion failures/,/## Failed flows/' "$REPORT_MD" || true
        fail "$label: $((total - passed)) of $total flows failed (see $REPORT_MD)"
    fi
    printf '\033[1;32m%s\033[0m: %s\n' "$label OK" "$flows_line"
}

# ---------------------------------------------------------------------------

section "Pre-flight: single-CPU pre-build (avoids cgroup-fenced parallel OOM)"
dotnet build "$HARNESS_PROJ" -m:1 -nologo

if [ "${SKIP_UNIT:-0}" != "1" ]; then
    section "1/4  Unit tests"
    dotnet test "$UNIT_PROJ" -m:1 -nologo --logger "console;verbosity=minimal"
fi

if [ "${SKIP_E2E:-0}" != "1" ]; then
    section "2/4  End-to-end tests (Testcontainers — broker + mongo per fixture)"
    dotnet test "$E2E_PROJ" -m:1 -nologo --logger "console;verbosity=minimal"
fi

if [ "${SKIP_HARNESS:-0}" != "1" ]; then
    section "3/4  Stress harness — soak ${HARNESS_DURATION}, no chaos"
    bring_up_broker
    rm -f "$REPORT_MD" out/report.json
    dotnet run --no-build --project "$HARNESS_PROJ" -- \
        --mode soak \
        --duration "$HARNESS_DURATION" \
        --rate 100 \
        --persistence inmemory \
        --chaos none \
        --flow-timeout "$FLOW_TIMEOUT"
    verify_harness_passed "Stress harness (no chaos)"
    teardown_compose
fi

if [ "${SKIP_CHAOS:-0}" != "1" ]; then
    section "4/4  Chaos harness — soak ${HARNESS_DURATION}, kill every ${CHAOS_INTERVAL} for ${CHAOS_DOWNTIME}"
    bring_up_broker
    rm -f "$REPORT_MD" out/report.json
    dotnet run --no-build --project "$HARNESS_PROJ" -- \
        --mode soak \
        --duration "$HARNESS_DURATION" \
        --rate 100 \
        --persistence inmemory \
        --chaos docker \
        --chaos-interval "$CHAOS_INTERVAL" \
        --chaos-downtime "$CHAOS_DOWNTIME" \
        --chaos-compose-file "$COMPOSE_FILE" \
        --flow-timeout "$FLOW_TIMEOUT"
    verify_harness_passed "Chaos harness"
    teardown_compose
fi

printf '\n\033[1;32mAll requested verification stages passed.\033[0m\n'
