#!/bin/zsh
# Copies the pinned mlx-audio-swift from InnerVoice's build and makes the speaker encoder
# public, then builds. Run InnerVoice's `swift build` once first so the checkout exists.
set -e
cd "${0:A:h}"
rm -rf mlx-audio-swift
rsync -a --exclude .git ../../.build/checkouts/mlx-audio-swift/ mlx-audio-swift/
sed -i '' 's/^    func extractSpeakerEmbedding(/    public func extractSpeakerEmbedding(/' mlx-audio-swift/Sources/MLXAudioTTS/Models/Qwen3TTS/Qwen3TTS.swift
cp ../../Package.resolved .
swift build -c release
echo "Run: .build/release/ShadeSpike <out folder>   (SPIKE_SEEDS=1 for the seed test, SPIKE_ONLY=a,b to pick conditions)"
