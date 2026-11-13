#!/bin/zsh
# The Pocket TTS stock voices for Windows: the Mac's clips (Resources/StockVoices/*.m4a, 16-bit
# Apple Lossless, 24 kHz mono) decoded to 16-bit WAV, sample for sample the same audio, so the
# Windows app (which can't read Apple Lossless everywhere) clones from exactly what the Mac
# does. Run on a Mac after the Mac's clips change:  windows/tools/make-stock-voices.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
out=windows/Resources/StockVoices
mkdir -p "$out"
rm -f "$out"/*.wav(N)
for clip in Resources/StockVoices/*.m4a; do
  name=$(basename "$clip" .m4a)
  afconvert -f WAVE -d LEI16@24000 -c 1 "$clip" "$out/$name.wav"
done
ls -l "$out"
