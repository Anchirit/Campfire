#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_BIN="${1:-${DOTNET_BIN:-dotnet}}"
"$DOTNET_BIN" build tests/Campfire.Tests/Campfire.Tests.csproj -c Release --nologo
python3 tests/run-fixture-tests.py "$DOTNET_BIN"
