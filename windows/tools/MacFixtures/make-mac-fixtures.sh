#!/bin/zsh
# Builds main.swift together with the Mac's Kit (Sources/InnerVoiceKit) into a throwaway tool
# and runs it: writes the Mac-made Pocket TTS fixtures into windows/Fixtures (see main.swift).
# Run on a Mac from anywhere: windows/tools/MacFixtures/make-mac-fixtures.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
kit=("${(@f)$(find "$root/Sources/InnerVoiceKit" -name '*.swift')}")
xcrun swiftc -O -module-name InnerVoiceKit -o "$out/mac-fixtures" "${kit[@]}" "$here/main.swift"
"$out/mac-fixtures" "$root/windows/Fixtures"
