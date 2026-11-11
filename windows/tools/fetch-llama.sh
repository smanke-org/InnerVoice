#!/bin/bash
# Downloads llama.cpp b11205 for Windows (the same build as the Mac app) into windows/native/
# and checks each zip's SHA-256. Not committed; run once after cloning, and in CI.
#   x64:   Vulkan build (NVIDIA, AMD, Intel GPUs) with every CPU backend
#   arm64: CPU build
set -euo pipefail
cd "$(dirname "$0")/.."

TAG="b11205"
fetch() {
  local name="$1" sha="$2" rid="$3"
  local dest="native/$rid"
  if [ -f "$dest/.tag" ] && [ "$(cat "$dest/.tag")" = "$TAG-$name" ]; then
    echo "llama.cpp $TAG $name already present"
    return
  fi
  local tmp; tmp="$(mktemp -d)"
  local zip="llama-$TAG-bin-$name.zip"
  echo "==> Downloading $zip"
  curl -fL --progress-bar -o "$tmp/$zip" "https://github.com/ggml-org/llama.cpp/releases/download/$TAG/$zip"
  local actual; actual=$(shasum -a 256 "$tmp/$zip" 2>/dev/null | awk '{print $1}' || sha256sum "$tmp/$zip" | awk '{print $1}')
  if [ "$actual" != "$sha" ]; then echo "Digest mismatch for $zip: expected $sha, got $actual" >&2; exit 1; fi
  rm -rf "$dest"; mkdir -p "$dest"
  # Only the libraries the app loads: llama, ggml and its backends, OpenMP.
  if command -v unzip >/dev/null; then
    unzip -q -j "$tmp/$zip" 'llama.dll' 'ggml*.dll' 'libomp*.dll' -d "$dest"
  else
    7z e -y -bso0 "$tmp/$zip" -o"$dest" 'llama.dll' 'ggml*.dll' 'libomp*.dll' >/dev/null
  fi
  rm -f "$dest/ggml-rpc.dll"
  echo "$TAG-$name" > "$dest/.tag"
  rm -rf "$tmp"
  ls "$dest"
}

fetch win-vulkan-x64 f3d08e7e342e2cb96ccddd1a208320c188d0a394844553a40a32c5e9903af563 win-x64
fetch win-cpu-arm64 39adbe8846a1228f0b16e93ba726e8c05c5af46d990441b272f7eed64f8ff455 win-arm64
