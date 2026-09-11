#!/usr/bin/env bash
# Builds libOC2NativeXboxInput.dylib from xbox_gamecontroller.swift. Called
# by the parent repo's top-level build.sh (which then copies the result into
# bundled-bepinex/BepInEx/native/ for the launcher app to ship); can also be
# run standalone for local testing.
#
# x86_64 because the game binary itself is x86_64-only (even on Apple
# Silicon, running under Rosetta) - a dylib loaded into that process must
# match. -target ...-macos13.0 (not an older version) works around Xcode
# Command Line Tools installs missing x86_64 slices of their Swift
# concurrency back-deployment compatibility libraries for older targets -
# a lower target fails to link with "symbol(s) not found for architecture
# x86_64" referencing swiftCompatibility56/swiftCompatibilityConcurrency.
set -euo pipefail
cd "$(dirname "$0")"

OUT="${1:-libOC2NativeXboxInput.dylib}"

swiftc -emit-library -o "$OUT" xbox_gamecontroller.swift \
  -framework GameController -target x86_64-apple-macos13.0

echo "Built: $(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")"
file "$OUT"
nm -gU "$OUT" | grep OC2Xbox
