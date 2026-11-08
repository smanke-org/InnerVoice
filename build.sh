#!/bin/zsh
# Builds Inner Voice into .build/app/Inner Voice.app (Developer ID when available,
# hardened runtime, not sandboxed) with the pinned llama.framework embedded.
#
# Usage: ./build.sh            release build (arm64)
#        ./build.sh --debug    debug build
#        ./build.sh --install  also replace /Applications/Inner Voice.app (quits the running copy)
#        ./build.sh --open     install, then open it
#        ./build.sh --universal  arm64 + x86_64 (release.sh uses this)
set -euo pipefail
here=${0:A:h}
name="InnerVoice"
display="Inner Voice"
bundle="com.smanke.InnerVoice"
config=release
[[ " $* " == *" --debug "* ]] && config=debug
args=(-c $config --package-path "$here")
[[ " $* " == *" --universal "* ]] && args+=(--arch arm64 --arch x86_64)

[[ -d "$here/Frameworks/llama.xcframework" ]] || "$here/Tools/fetch_llama.sh"

# Ask SwiftPM where the product goes (Swift 6.4 moved it), delete it, and require the
# build to write it again, so the packaged binary is provably from this build.
bindir="$(swift build "${args[@]}" --show-bin-path)"
rm -f "$bindir/$name"
marker=$(mktemp); trap 'rm -f "$marker"' EXIT; sleep 1
swift build "${args[@]}" 2>&1 | grep -E "error|warning: |Compiling|Build complete" | grep -v "^\[" | grep -v Compiling || true
if [[ ! -x "$bindir/$name" || ! "$bindir/$name" -nt "$marker" ]]; then
  echo "build failed: no fresh $bindir/$name"
  swift build "${args[@]}" 2>&1 | grep -E "error" | head -30
  exit 1
fi

app="$here/.build/app/$display.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$app/Contents/Frameworks"
cp "$bindir/$name" "$app/Contents/MacOS/$name"
# release.sh sets BUILD_NUMBER to the version, so a release reads "v1.0.0", not "v1.0.0 (2026…)".
sed -e "s/__BUILD__/${BUILD_NUMBER:-$(date +%Y%m%d%H%M)}/" "$here/Info.plist.in" > "$app/Contents/Info.plist"
ditto "$here/Frameworks/llama.xcframework/macos-arm64_x86_64/llama.framework" "$app/Contents/Frameworks/llama.framework"
# The icon: Tools/make_icns.sh renders Resources/AppIcon.icns (house element-tile style).
[[ -f "$here/Resources/AppIcon.icns" ]] || "$here/Tools/make_icns.sh"
cp -X "$here/Resources/AppIcon.icns" "$app/Contents/Resources/"
[[ -d "$here/Resources/Help" ]] && ditto "$here/Resources/Help" "$app/Contents/Resources/Help"
xattr -cr "$app"

identity="${SIGN_IDENTITY:-}"
[[ -n "$identity" ]] || identity=$(security find-identity -v -p codesigning 2>/dev/null | grep -m1 -o '"Developer ID Application: [^"]*"' | tr -d '"' || true)
[[ -n "$identity" ]] || identity=$(security find-identity -v -p codesigning 2>/dev/null | grep -m1 -o '"Apple Development: [^"]*"' | tr -d '"' || true)
[[ -n "$identity" ]] || identity=-
ts=(--timestamp); [[ "$identity" == "-" ]] && ts=()
codesign --force --options runtime $ts --sign "$identity" "$app/Contents/Frameworks/llama.framework"
codesign --force --options runtime $ts --identifier "$bundle" \
  --entitlements "$here/Resources/InnerVoice.entitlements" --sign "$identity" "$app"
codesign --verify --deep --strict "$app"
echo "built $app ($config, signed: $identity)"

if [[ " $* " == *" --install "* || " $* " == *" --open "* ]]; then
  pkill -x "$name" 2>/dev/null && sleep 1 || true
  rsync -a --delete "$app/" "/Applications/$display.app/"
  echo "installed /Applications/$display.app"
  [[ " $* " == *" --open "* ]] && open "/Applications/$display.app"
fi
exit 0
