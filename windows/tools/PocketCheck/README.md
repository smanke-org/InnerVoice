# PocketCheck

Shows that the Pocket TTS files the Windows app downloads are Kyutai's model and nothing else
(the user wants no Chinese models; see docs/WINDOWS-PLAN.md § Pocket TTS). Not part of the app.

The ONNX files are an existing export, so nothing is exported here: KevinAHM/pocket-tts-onnx
at commit 58a6d00cf13d239b6748cb0769f35c580a8f606c, the five fp32 graphs at its root
(`onnx/*.onnx`, the original January 2026 English model) and `tokenizer.model`, pinned by
SHA-256 in `src/InnerVoice.Core/Speech/Neural/VoicePacks.cs`. `verify_weights.py` compares
every learned weight in them with the Mac's model, mlx-community/pocket-tts at
cbf71d5f6657bbc3f4bc02f85ee408261225bec7 (Kyutai's tts_b6369a24 weights in bfloat16).

```
python3 -m venv .venv                       # inside this folder; ignored by git
.venv/bin/pip install -r requirements.txt   # about 100 MB, only into .venv
# the ONNX files (KevinAHM/pocket-tts-onnx@58a6d00…/onnx/*.onnx) into <onnx folder>, and
# model.safetensors from mlx-community/pocket-tts@cbf71d5…
.venv/bin/python -I verify_weights.py <onnx folder> <model.safetensors>
```

Result on 2026-11-13: 117,857,280 of 117,869,862 values (99.989%) are Kyutai's weights,
bit for bit after rounding to bfloat16: all of the flow transformer, the flow net, the text
embedding table, the Mimi decoder, and the Mimi encoder with the speaker projection. The rest
(12,582 values, all in mimi_encoder.onnx) are zero tensors for the streaming convolutions'
padding and the 32 rotary-embedding frequencies (10000^(-i/32)), which are constants, not
weights. Three of the Mac's 213 tensors aren't found because they're one value each or are
recomputed (two one-element biases, the flow net's time-embedding frequencies). The graphs use
only standard ONNX operators (opset 14 to 18), exported with PyTorch 2.9.1.

The tokenizer is the same file as Kyutai's own (`kyutai/pocket-tts-without-voice-cloning`,
SHA-256 d461765a…bc3f6). The stock voices are the Mac's clips (Orpheus's voices and Alba
MacKenna's, from kyutai/tts-voices), copied by `tools/make-stock-voices.sh`.
