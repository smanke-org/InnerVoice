#!/bin/zsh
# Quick compile check: prints only errors (file:line: message), deduplicated.
cd ${0:A:h:h}
target=${1:-InnerVoice}
swift build --target $target 2>&1 | grep -E "error:" | grep -v "^error: Build failed\|SwiftCompile\|Command line" | sed -E 's/\x1b\[[0-9;]*m//g; s#^.*/Sources/#Sources/#' | grep -E "^Sources|error:" | sort -u | head -${LIMIT:-40}
echo "exit: ${pipestatus[1]}"
