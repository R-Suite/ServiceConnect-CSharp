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
prebuild_solution() {
    local solution_path="$1"
    if [ ! -f "$solution_path" ]; then
        echo "prebuild_solution: solution not found: $solution_path" >&2
        return 1
    fi
    echo "Pre-building $(basename "$solution_path") sequentially (-m:1)..."
    dotnet build "$solution_path" -m:1 --nologo --verbosity quiet
}
