#!/usr/bin/env bash
# ToyCity's GPU test (Linux and macOS). Builds the game, starts a server of its own on port 5189 with an empty save
# folder (so your own cities are never touched), runs the Godot client's --gpu-test and stops the server again.
# The report lands in gpu-test/ at the top of the repository, or in the folder given as the first argument.
#
#   GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 tools/gpu-test/gpu-test.sh [REPORT_DIR]
#
# GODOT_ARGS adds options for Godot itself (CI picks the compatibility renderer with it), GPU_TEST_ARGS options for
# the test (CI holds each view for a second and quits: "--gpu-hold 1 --gpu-quit").
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
godot="${GODOT:-$(command -v godot4 || command -v godot || true)}"
if [ -z "$godot" ]; then
  echo "Set GODOT to your Godot 4.7.2 .NET executable, for example:"
  echo "  GODOT=~/Godot/Godot_v4.7.2-stable_mono_linux.x86_64 $0"
  exit 1
fi
out="${1:-$root/gpu-test}"
port=5189
saves="$(mktemp -d)"
echo "Building the server and the client…"
dotnet build "$root/src/ToyCity.Server" -c Release -v q --nologo
dotnet build "$root/clients/godot" -v q --nologo
echo "Starting a test server on port $port…"
(cd "$root" && ToyCity__SaveDir="$saves" exec dotnet "$root/src/ToyCity.Server/bin/Release/net10.0/ToyCity.Server.dll" --urls "http://localhost:$port") > "$saves/server.log" 2>&1 &
server=$!
trap 'kill $server 2>/dev/null || true; rm -rf "$saves"' EXIT
for _ in $(seq 60); do curl -sf "http://localhost:$port/health" > /dev/null && break; sleep 1; done
"$godot" --headless --path "$root/clients/godot" --import > /dev/null 2>&1 || true
echo "Running the GPU test (about three minutes)…"
# shellcheck disable=SC2086
"$godot" ${GODOT_ARGS:-} --path "$root/clients/godot" -- --server "ws://localhost:$port/ws" --gpu-test "$out" ${GPU_TEST_ARGS:-}
echo "The reports are in $out"
