#!/bin/bash
# Downloads the pinned llama.cpp xcframework into Frameworks/ and checks its digest.
# The framework is not committed (about 200 MB unpacked); run this once after cloning.
set -euo pipefail
cd "$(dirname "$0")/.."

TAG="b11205"
SHA256="bbf935b50774634bd3ecbd601f98ddfc49342bbaf67010c23dd346d67e8a82eb"
ZIP="llama-$TAG-xcframework.zip"
URL="https://github.com/ggml-org/llama.cpp/releases/download/$TAG/$ZIP"

if [ -f Frameworks/llama.xcframework/.tag ] && [ "$(cat Frameworks/llama.xcframework/.tag)" = "$TAG" ]; then
  echo "llama.xcframework $TAG already present"
  exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
echo "==> Downloading $ZIP"
curl -fL --progress-bar -o "$TMP/$ZIP" "$URL"
ACTUAL=$(shasum -a 256 "$TMP/$ZIP" | awk '{print $1}')
if [ "$ACTUAL" != "$SHA256" ]; then
  echo "Digest mismatch: expected $SHA256, got $ACTUAL" >&2
  exit 1
fi
unzip -q "$TMP/$ZIP" -d "$TMP/x"
mkdir -p Frameworks
rm -rf Frameworks/llama.xcframework
mv "$TMP/x/build-apple/llama.xcframework" Frameworks/
echo "$TAG" > Frameworks/llama.xcframework/.tag
echo "Installed Frameworks/llama.xcframework ($TAG)"
