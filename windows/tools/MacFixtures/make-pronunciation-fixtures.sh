#!/bin/zsh
# Builds Pronunciations/main.swift together with the Mac's Kit (Sources/InnerVoiceKit) into a
# throwaway tool and runs it: writes the Mac-made pronunciation files into
# windows/Fixtures/pronunciations and checks the Mac reads the ones the Windows tests exported.
# Run on a Mac from anywhere: windows/tools/MacFixtures/make-pronunciation-fixtures.sh
# KIT_ROOT=<checkout> takes the Kit from another checkout (a branch with the Mac's newer Kit).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
kitroot=${KIT_ROOT:-$root}
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
kit=("${(@f)$(find "$kitroot/Sources/InnerVoiceKit" -name '*.swift')}")
xcrun swiftc -O -module-name InnerVoiceKit -o "$out/pronunciation-fixtures" "${kit[@]}" "$here/Pronunciations/main.swift"
"$out/pronunciation-fixtures" "$root/windows/Fixtures"
