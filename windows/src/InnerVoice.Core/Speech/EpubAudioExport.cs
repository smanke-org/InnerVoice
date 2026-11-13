// Ported from the Mac's Sources/InnerVoice/Export/EpubExport.swift (EpubExporter.write): the
// book's text and its voices in one EPUB 3 file, with Media Overlays so each sentence
// highlights as it's read in reading apps that play read-along books.

namespace InnerVoice.Core.Speech;

public static class EpubAudioExport
{
    /// Reads the picked chapters (with narration) and writes the book to `path`. Everything is
    /// made in a local temporary folder and moved into place at the end (cloud folders). The
    /// audio is M4A, which EPUB reading apps play; there is no WAV choice.
    public static async Task Write(Project project, BookIndex index, IReadOnlyList<int> picked, string path, VoiceResolver resolver,
                                   IAudioEncoder encoder, SpeechRenderer? renderer = null, Action<int, int, string>? progress = null,
                                   PauseSettings? pauses = null, CancellationToken ct = default)
    {
        pauses ??= PauseSettings.Current;
        var staging = Path.Combine(Path.GetTempPath(), $"InnerVoice EPUB {Guid.NewGuid():N}");
        var audioFolder = Path.Combine(staging, "Audio");
        Directory.CreateDirectory(audioFolder);
        try
        {
            var language = ReadAlongEpub.LanguageOf(project);
            var chapters = picked.Distinct().Order().Where(c => c >= 0 && c < project.Chapters.Count).ToList();
            var pages = new List<ReadAlongEpub.Page>();
            var parts = new List<AudioPart>();
            for (int n = 0; n < chapters.Count; n++)
            {
                var segments = SpeechScript.Segments(chapters[n], project, index, includeNarration: true, pauses: pauses);
                pages.Add(ReadAlongEpub.MakePage(project.Chapters[chapters[n]], n + 1, ReadAlongEpub.Segments(segments), language));
                parts.Add(new AudioPart($"c{n + 1:000}", segments));
            }
            var timing = parts.Select(_ => new Dictionary<string, (double Begin, double End)>()).ToList();
            var titles = chapters.Select(c => AudioExporter.ChapterTitle(project, c)).ToList();
            var files = await AudioExporter.Write(parts, audioFolder, AudioFormat.M4a, resolver, renderer, encoder,
                timing: (p, seg, begin, end) => timing[p][seg.Id] = (begin, end),
                progress: (done, total, part) =>
                {
                    var what = part.Length > 1 && int.TryParse(part[1..], out var k) && k >= 1 && k <= titles.Count ? titles[k - 1] : part;
                    progress?.Invoke(done, total, what);
                },
                pauses: pauses, ct: ct).ConfigureAwait(false);
            var byPart = files.ToDictionary(f => f.Part);

            var chapterFiles = new List<ReadAlongEpub.ChapterFile>();
            for (int n = 0; n < pages.Count; n++)
            {
                var audio = byPart.GetValueOrDefault(n);
                double duration = audio?.Seconds ?? 0;
                var clips = audio is null ? [] : ReadAlongEpub.Clips(pages[n].Fragments, timing[n], duration);
                chapterFiles.Add(new ReadAlongEpub.ChapterFile(titles[n], pages[n].Xhtml, clips.Count == 0 ? null : audio!.Path, duration, clips));
            }
            var book = new ReadAlongEpub.Book(project.Title, project.Author, language, ReadAlongEpub.Identifier(project.Title, project.Author),
                                              DateTime.UtcNow, chapterFiles);
            progress?.Invoke(1, 1, "");
            var output = Path.Combine(staging, "book.epub");
            await Task.Run(() => ReadAlongEpub.Write(book, output), ct).ConfigureAwait(false);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null) Directory.CreateDirectory(dir);
            File.Move(output, path, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }
}
