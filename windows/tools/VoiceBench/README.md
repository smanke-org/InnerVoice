# VoiceBench

Measures the local voices (Pocket TTS and Orpheus on ONNX Runtime) on the computer it runs on.
Not part of the app.

```
dotnet run --project tools/VoiceBench -c Release -- --hardware
dotnet run --project tools/VoiceBench -c Release -- --bench "%LOCALAPPDATA%\InnerVoice\Voice Models" out
dotnet run --project tools/VoiceBench -c Release -- --bench <folder> out pocket      (or orpheus)
dotnet run --project tools/VoiceBench -c Release -- --clone <folder> recording.wav out
dotnet run --project tools/VoiceBench -c Release -- --probe <model.onnx>
dotnet run --project tools/VoiceBench -c Release -- --check out\*.wav
```

`--bench` needs the voice packs in the Voice Models layout (download them in the app's
Settings › Voices first, or lay out `pocket\onnx\*.onnx`, `pocket\tokenizer.model` and
`pocket\LICENSE` from KevinAHM/pocket-tts-onnx at the commit in `VoicePacks.cs`). With Pocket
TTS it speaks every stock voice (`pocket-voice-*.wav`), Tara in every tone (`pocket-tone-*`)
and as a child (`pocket-child-tara`), two short lines at the even and the angry temperature
(`pocket-short-*`, to hear that a line stops when it's said) and three lines with Dan
(`pocket-line-*`); with Orpheus the same three lines. It prints seconds of audio per second of
work, where the models ran (graphics card or CPU), and each file's pitch. `--clone` makes a
voice from a recording the way Clone a Voice › On This PC does (up to 15 seconds) and speaks
three lines with it. On Windows, build with `-r win-x64` or `-r win-arm64` to get DirectML.

On a Mac (CPU only) it is how the numbers in docs/WINDOWS-PLAN.md § Local voices were measured.
Use only made-up sentences, never a real manuscript.
