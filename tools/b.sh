#!/bin/zsh
# Quick compile check: prints only errors (file:line: message), deduplicated.
cd ${0:A:h:h}
swift build ${@:---target InnerVoice} 2>&1 | grep -E "error:|warning: .*(never|unused|deprecated)" | grep -v "^error: Build failed\|SwiftCompile\|Command line" | sed -E 's/\x1b\[[0-9;]*m//g' | sort -u | head -${LIMIT:-40}
echo "exit: ${pipestatus[1]}"
