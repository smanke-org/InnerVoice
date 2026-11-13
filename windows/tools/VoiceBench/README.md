# VoiceBench

Measures the local voices (Qwen3-TTS and Orpheus on ONNX Runtime) on the computer it runs on.
Not part of the app.

```
dotnet run --project tools/VoiceBench -c Release -- --hardware
dotnet run --project tools/VoiceBench -c Release -- --bench "%LOCALAPPDATA%\InnerVoice\Voice Models" out
dotnet run --project tools/VoiceBench -c Release -- --bench <folder> out qwen      (or orpheus)
dotnet run --project tools/VoiceBench -c Release -- --probe <model.onnx>
dotnet run --project tools/VoiceBench -c Release -- --check out\*.wav
```

`--bench` needs the voice packs in the Voice Models layout (download them in the app's
Settings › Voices first). It designs a voice sample, clones three lines from it with
Qwen3-TTS, speaks the same lines with Orpheus, writes the WAV files, and prints seconds of
audio per second of work, where the models ran (graphics card or CPU), and each file's
pitch. On Windows, build with `-r win-x64` or `-r win-arm64` to get DirectML.

On a Mac (CPU only) it is how the numbers in docs/WINDOWS-PLAN.md § Local voices were measured.
Use only made-up sentences, never a real manuscript.
