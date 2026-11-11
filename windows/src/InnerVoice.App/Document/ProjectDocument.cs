using InnerVoice.Core;

namespace InnerVoice.App.Document;

/// One open book: the project, its index, the files that ride along in the archive, and
/// undo. Every edit goes through `Change`, one undo step each, like the Mac's
/// `change(_:undoManager:)`.
public sealed class ProjectDocument
{
    public Project Project { get; private set; }
    public BookIndex Index { get; private set; }
    /// Pronunciation samples (Audio/ in the archive).
    public Dictionary<string, byte[]> Audio { get; private set; }
    /// The imported manuscript file (Original/ in the archive).
    public (string Name, byte[] Data)? Original { get; private set; }

    public string? FilePath { get; private set; }
    public string DisplayName => FilePath is null ? Project.Title : Path.GetFileNameWithoutExtension(FilePath);
    public bool IsDirty { get; private set; }
    /// Made by a newer InnerVoice: shown, never saved over.
    public bool IsReadOnly { get; }

    /// Raised after every change, undo and redo. `TextChanged` is true when paragraphs moved.
    public event Action<bool>? Changed;
    public event Action? Saved;

    readonly List<(string Name, Snapshot State)> undo = [];
    readonly List<(string Name, Snapshot State)> redo = [];
    const int UndoLimit = 100;
    DateTime? fileTimeAtLoad;
    CancellationTokenSource? autosave;

    sealed record Snapshot(Project Project, Dictionary<string, byte[]> Audio);

    ProjectDocument(Project project, Dictionary<string, byte[]> audio, (string, byte[])? original, string? path, bool readOnly)
    {
        Project = project;
        Index = new BookIndex(project);
        Audio = audio;
        Original = original;
        FilePath = path;
        IsReadOnly = readOnly;
        if (path is not null && File.Exists(path)) fileTimeAtLoad = File.GetLastWriteTimeUtc(path);
    }

    public static ProjectDocument Open(string path)
    {
        var contents = ProjectArchive.Read(File.ReadAllBytes(path));
        var project = ProjectJson.Decode(contents.ProjectJson);
        return new ProjectDocument(project, contents.Audio, contents.Original, path, project.Format > Project.CurrentFormat);
    }

    /// A new project from an imported manuscript, not saved yet.
    public static ProjectDocument New(Project project, (string Name, byte[] Data)? original) =>
        new(project, [], original, null, false) { IsDirty = true };

    /// A project shown without a file (the sample book).
    public static ProjectDocument Unsaved(Project project) => new(project, [], null, null, false);

    // MARK: Changes

    public string? UndoName => undo.Count > 0 ? undo[^1].Name : null;
    public string? RedoName => redo.Count > 0 ? redo[^1].Name : null;

    /// Runs `edit` on the project as one undo step named `name`.
    public void Change(string name, Action<Project> edit, bool textChanged = false)
    {
        var before = new Snapshot(Project.Copy(), new Dictionary<string, byte[]>(Audio));
        edit(Project);
        undo.Add((name, before));
        if (undo.Count > UndoLimit) undo.RemoveAt(0);
        redo.Clear();
        Committed(textChanged);
    }

    /// A change the narrator didn't make (automatic colors): no undo step.
    public void ApplyQuietly(Action<Project> edit)
    {
        edit(Project);
        Committed(false);
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var (name, state) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add((name, new Snapshot(Project, Audio)));
        Project = state.Project;
        Audio = state.Audio;
        Committed(true);
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var (name, state) = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add((name, new Snapshot(Project, Audio)));
        Project = state.Project;
        Audio = state.Audio;
        Committed(true);
    }

    void Committed(bool textChanged)
    {
        Index = new BookIndex(Project);
        IsDirty = true;
        Changed?.Invoke(textChanged);
        ScheduleAutosave();
    }

    // MARK: Saving

    /// Saves a couple of seconds after the last change, like the Mac's autosave in place.
    void ScheduleAutosave()
    {
        if (FilePath is null || IsReadOnly) return;
        autosave?.Cancel();
        var cts = autosave = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) TrySave(out _); });
        }, TaskScheduler.Default);
    }

    /// True when another computer (or app) changed the file since it was opened or saved.
    public bool ChangedOnDisk =>
        FilePath is not null && fileTimeAtLoad is { } t && File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) > t.AddSeconds(1);

    public bool TrySave(out string? error, string? path = null, bool overwriteNewer = false)
    {
        error = null;
        path ??= FilePath;
        if (path is null) { error = "Choose where to save the project first."; return false; }
        if (IsReadOnly && path == FilePath) { error = "A newer version of InnerVoice made this project, so it can't be saved here."; return false; }
        if (path == FilePath && ChangedOnDisk && !overwriteNewer)
        {
            error = "changed-on-disk";
            return false;
        }
        try
        {
            var data = ProjectArchive.Write(new ProjectArchive.Contents
            {
                ProjectJson = ProjectJson.Encode(Project),
                Original = Original,
                Audio = Audio,
            });
            WriteAtomically(path, data);
            FilePath = path;
            fileTimeAtLoad = File.GetLastWriteTimeUtc(path);
            IsDirty = false;
            Saved?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// Writes beside the file, then swaps it in, so a crash or a sync never leaves half a
    /// file (cloud folders like OneDrive replace files under open windows).
    static void WriteAtomically(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllBytes(tmp, data);
        try
        {
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows()) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                else File.Move(tmp, path, overwrite: true);
            }
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public void FlushAutosave()
    {
        autosave?.Cancel();
        if (IsDirty && FilePath is not null && !IsReadOnly) TrySave(out _);
    }

    public void SetAudio(string name, byte[]? data)
    {
        if (data is null) Audio.Remove(name); else Audio[name] = data;
    }
}
