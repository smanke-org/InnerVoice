// Ported from Sources/InnerVoiceKit/RevisionMerger.swift. The tone rules run after the
// speaker rules, as on the Mac.

using System.Text;
using InnerVoice.Core.Import;

namespace InnerVoice.Core;

/// Brings a revised manuscript into an existing project. Unchanged paragraphs keep their
/// identity, so every tag, fact source, recorded mark and retake stays put; lightly edited
/// paragraphs keep the speakers of lines whose words still match; new text is found and
/// tagged with the rules, ready for the AI.
public static class RevisionMerger
{
    public sealed record Result(Project Project, RevisionRecord Record);

    /// Never changes `old`; the result's chapters are copies of the book's.
    public static Result Merge(Project old, ImportedBook book, string fileName)
    {
        var oldIndex = new BookIndex(old);
        var oldParas = old.Chapters.SelectMany(c => c.Paragraphs.Select(p => (Title: c.Title, Paragraph: p))).ToList();
        var newChapters = book.Chapters.ConvertAll(c => c.Copy());
        var newParas = newChapters.SelectMany(c => c.Paragraphs).ToList();
        // Swift compares Strings by canonical equivalence; NFC then ordinal is the same test.
        var oldKeys = oldParas.ConvertAll(p => TextMatch.Normalize(p.Paragraph.Text).Normalize(NormalizationForm.FormC));
        var newKeys = newParas.ConvertAll(p => TextMatch.Normalize(p.Text).Normalize(NormalizationForm.FormC));

        // 1. Exact matches in order (a diff), then pair what is left by similarity.
        var newToOld = new Dictionary<int, int>();
        var changed = new HashSet<int>();
        var (removedList, insertedList) = Diff(oldKeys, newKeys);
        var removedOld = removedList.ToHashSet();
        var insertedNew = insertedList.ToHashSet();
        int o = 0, n = 0;
        while (o < oldKeys.Count && n < newKeys.Count)
        {
            if (removedOld.Contains(o)) { o++; continue; }
            if (insertedNew.Contains(n)) { n++; continue; }
            newToOld[n] = o;
            o++;
            n++;
        }
        // Within each gap between exact matches, pair edited paragraphs.
        var anchors = newToOld.OrderBy(kv => kv.Key).Select(kv => (New: kv.Key, Old: kv.Value)).ToList();
        anchors.Add((newKeys.Count, oldKeys.Count));
        var gaps = new List<(int OldStart, int OldEnd, int NewStart, int NewEnd)>();
        int prevNew = -1, prevOld = -1;
        foreach (var (nn, oo) in anchors)
        {
            if (nn - prevNew > 1 && oo - prevOld > 1) gaps.Add((prevOld + 1, oo, prevNew + 1, nn));
            prevNew = nn;
            prevOld = oo;
        }
        var pairedOld = newToOld.Values.ToHashSet();
        foreach (var (oldStart, oldEnd, newStart, newEnd) in gaps)
        {
            int cursor = oldStart;
            for (int nn = newStart; nn < newEnd; nn++)
            {
                int bestOld = -1;
                double bestScore = 0;
                for (int oo = cursor; oo < oldEnd; oo++)
                {
                    if (pairedOld.Contains(oo)) continue;
                    double s = TextMatch.Similarity(oldParas[oo].Paragraph.Text, newParas[nn].Text);
                    if (s >= 0.5 && s > bestScore) { bestOld = oo; bestScore = s; }
                }
                if (bestOld >= 0)
                {
                    newToOld[nn] = bestOld;
                    pairedOld.Add(bestOld);
                    changed.Add(nn);
                    cursor = bestOld + 1;
                }
            }
        }

        // 2. Give matched paragraphs their old identity.
        for (int i = 0; i < newParas.Count; i++)
            if (newToOld.TryGetValue(i, out var oo)) newParas[i].Id = oldParas[oo].Paragraph.Id;
        // Chapters keep their id when their title matches an old one, so outlines stay stable.
        foreach (var chapter in newChapters)
        {
            var title = TextMatch.Normalize(chapter.Title);
            var match = old.Chapters.Find(c => TextMatch.Normalize(c.Title) == title);
            if (match is not null) chapter.Id = match.Id;
        }

        // 3. Lines: unchanged paragraphs keep theirs; edited ones match by words.
        var found = DialogueFinder.FindAll(newChapters);
        var oldLinesByParagraph = new Dictionary<Guid, List<Line>>();
        foreach (var l in old.Lines)
        {
            if (!oldLinesByParagraph.TryGetValue(l.ParagraphID, out var list)) oldLinesByParagraph[l.ParagraphID] = list = [];
            list.Add(l);
        }
        var changedParagraphIDs = new HashSet<Guid>();
        for (int i = 0; i < newParas.Count; i++)
            if (changed.Contains(i)) changedParagraphIDs.Add(newParas[i].Id);
        var newByID = new Dictionary<Guid, Paragraph>();
        foreach (var p in newParas) newByID.TryAdd(p.Id, p);
        var lines = new List<Line>();
        foreach (var line in found)
        {
            if (!oldLinesByParagraph.TryGetValue(line.ParagraphID, out var olds)) { lines.Add(line); continue; }
            if (!changedParagraphIDs.Contains(line.ParagraphID) && olds.Find(x => x.Range == line.Range) is { } same)
            {
                lines.Add(same.Copy());
                continue;
            }
            // Edited paragraph: keep the speaker of the old line with the same words.
            var newText = newByID.TryGetValue(line.ParagraphID, out var np) ? np.Substring(line.Range) : "";
            var oldPara = old.Paragraph(line.ParagraphID, oldIndex);
            Line? best = null;
            double bestScore = 0;
            foreach (var ol in olds)
            {
                double s = TextMatch.Similarity(oldPara?.Substring(ol.Range) ?? "", newText);
                if (s >= 0.6 && s > bestScore) { best = ol; bestScore = s; }
            }
            if (best is not null)
            {
                line.Id = best.Id;
                line.Kind = best.Kind;
                line.SpeakerID = best.SpeakerID;
                line.Source = best.Source;
                line.Confidence = best.Confidence;
            }
            lines.Add(line);
        }

        var project = old.Copy();
        project.Chapters = newChapters;
        project.Lines = lines;
        var keptIDs = newParas.Select(p => p.Id).ToHashSet();
        // Recorded audio of an edited paragraph no longer matches the text.
        project.Recorded = old.Recorded.Where(id => keptIDs.Contains(id) && !changedParagraphIDs.Contains(id)).ToHashSet();
        project.Retakes = project.Retakes.Where(r => keptIDs.Contains(r.ParagraphID)).ToList();
        foreach (var c in project.Characters)
            foreach (var f in c.Facts)
                if (f.SourceParagraphID is { } s && !keptIDs.Contains(s)) f.SourceParagraphID = null;
        foreach (var pr in project.Pronunciations)
            if (pr.FirstParagraphID is { } s && !keptIDs.Contains(s)) pr.FirstParagraphID = null;
        RuleAttribution.Apply(project);
        ToneRules.Apply(project);

        var matchedOld = newToOld.Values.ToHashSet();
        var removed = oldParas.Select((p, i) => (p, i))
            .Where(x => !matchedOld.Contains(x.i) && Words.Count(x.p.Paragraph.Text) > 0)
            .Select(x => new RemovedParagraph { Text = x.p.Paragraph.Text, ChapterTitle = x.p.Title })
            .ToList();
        var record = new RevisionRecord
        {
            FileName = fileName,
            Added = newParas.Where((_, i) => !newToOld.ContainsKey(i)).Select(p => p.Id).ToList(),
            Changed = newParas.Where(p => changedParagraphIDs.Contains(p.Id)).Select(p => p.Id).ToList(),
            Removed = removed,
            Unchanged = newToOld.Count - changed.Count,
        };
        project.Revisions.Add(record.Copy());
        project.SourceFileName = fileName;
        return new Result(project, record);
    }

    /// The offsets Swift's `newKeys.difference(from: oldKeys)` removes from `old` and inserts
    /// into `new`, each ascending. A port of the Swift standard library's linear-space Myers
    /// diff (`_linearSpaceMyers` in Diffing.swift): trim the common start and end, then split
    /// each box at its middle snake, with the same search order and tie-breaks, so equally
    /// short scripts come out the same. Time O((N+M)·D), memory O(N+M).
    public static (List<int> Removed, List<int> Inserted) Diff(IReadOnlyList<string> oldKeys, IReadOnlyList<string> newKeys)
    {
        var removed = new List<int>();
        var inserted = new List<int>();
        bool Same(int o, int n) => string.Equals(oldKeys[o], newKeys[n], StringComparison.Ordinal);
        var box = Shrunk(new Box(0, 0, oldKeys.Count, newKeys.Count), Same);
        // The forward and backward k-vectors, indexed -size-1 … size+1.
        int size = box.Size + 1;
        var forward = new int[2 * size + 1];
        var backward = new int[2 * size + 1];
        var stack = new Stack<Box>();
        var current = box;
        while (true)
        {
            var snake = MiddleSnake(current, forward, backward, size, Same, removed, inserted);
            if (snake is not { } s)
            {
                if (stack.Count == 0) break;
                current = stack.Pop();
                continue;
            }
            var head = Shrunk(new Box(current.Left, current.Top, s.Left, s.Top), Same);
            var tail = Shrunk(new Box(s.Right, s.Bottom, current.Right, current.Bottom), Same);
            current = head;
            stack.Push(tail);
        }
        removed.Sort();
        inserted.Sort();
        return (removed, inserted);
    }

    /// A region of the edit graph: old runs left to right, new top to bottom.
    readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int Size => Width + Height;
        public int Delta => Width - Height;
        public bool IsOdd => Delta % 2 != 0;
        public int Max => (Size + 1) / 2;
    }

    static Box Shrunk(Box r, Func<int, int, bool> same)
    {
        int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        while (left < right && top < bottom && same(left, top)) { left++; top++; }
        while (right > left && bottom > top && same(right - 1, bottom - 1)) { right--; bottom--; }
        return new Box(left, top, right, bottom);
    }

    /// Finds the box's middle snake and records the one edit leading into it; null when the
    /// box needed at most one edit (recorded) and nothing is left to split.
    static Box? MiddleSnake(Box box, int[] fwd, int[] bwd, int off, Func<int, int, bool> same, List<int> removed, List<int> inserted)
    {
        if (box.Size <= 1)
        {
            if (box.Size == 1)
            {
                if (box.Width == 1) removed.Add(box.Left);
                else inserted.Add(box.Top);
            }
            return null;
        }
        fwd[off + 1] = box.Left;
        bwd[off + 1] = box.Bottom;
        for (int depth = 0; depth <= box.Max; depth++)
        {
            if (box.IsOdd)
            {
                if (Forward(box, depth, fwd, bwd, off, same, removed, inserted) is { } r) return r;
                Backward(box, depth, fwd, bwd, off, same, removed, inserted);
            }
            else
            {
                Forward(box, depth, fwd, bwd, off, same, removed, inserted);
                if (Backward(box, depth, fwd, bwd, off, same, removed, inserted) is { } r) return r;
            }
        }
        throw new InvalidOperationException("Diff found no middle snake.");
    }

    static Box? Forward(Box box, int depth, int[] fwd, int[] bwd, int off, Func<int, int, bool> same, List<int> removed, List<int> inserted)
    {
        for (int k = depth; k >= -depth; k -= 2)
        {
            int newRight, newBottom, newLeft, newTop, editIndex;
            bool isInsertion;
            if (k == -depth || (k != depth && fwd[off + k - 1] < fwd[off + k + 1]))
            {
                newRight = fwd[off + k + 1];
                newLeft = newRight;
                newBottom = box.Top + (newRight - box.Left) - k;
                isInsertion = true;
                editIndex = newBottom - 1;
            }
            else
            {
                newLeft = fwd[off + k - 1];
                newRight = newLeft + 1;
                newBottom = box.Top + (newRight - box.Left) - k;
                isInsertion = false;
                editIndex = newLeft;
            }
            newTop = depth == 0 || newRight != newLeft ? newBottom : newBottom - 1;
            while (newRight < box.Right && newBottom < box.Bottom && same(newRight, newBottom)) { newRight++; newBottom++; }
            fwd[off + k] = newRight;
            // Only odd boxes check for overlap on the way forward.
            if (!box.IsOdd) continue;
            int c = k - box.Delta;
            if (c >= -(depth - 1) && c <= depth - 1 && newBottom >= bwd[off + c])
            {
                (isInsertion ? inserted : removed).Add(editIndex);
                return new Box(newLeft, newTop, newRight, newBottom);
            }
        }
        return null;
    }

    static Box? Backward(Box box, int depth, int[] fwd, int[] bwd, int off, Func<int, int, bool> same, List<int> removed, List<int> inserted)
    {
        for (int c = depth; c >= -depth; c -= 2)
        {
            int k = c + box.Delta;
            int newLeft, newTop, newRight, newBottom, editIndex;
            bool isInsertion;
            if (c == -depth || (c != depth && bwd[off + c - 1] > bwd[off + c + 1]))
            {
                newTop = bwd[off + c + 1];
                newBottom = newTop;
                newLeft = box.Left + (newTop - box.Top) + k;
                isInsertion = false;
                editIndex = newLeft;
            }
            else
            {
                newBottom = bwd[off + c - 1];
                newTop = newBottom - 1;
                newLeft = box.Left + (newTop - box.Top) + k;
                isInsertion = true;
                editIndex = newTop;
            }
            newRight = depth == 0 || newTop != newBottom ? newLeft : newLeft + 1;
            while (newLeft > box.Left && newTop > box.Top && same(newLeft - 1, newTop - 1)) { newLeft--; newTop--; }
            bwd[off + c] = newTop;
            // Only even boxes check for overlap on the way back.
            if (box.IsOdd) continue;
            if (k >= -depth && k <= depth && newLeft <= fwd[off + k])
            {
                (isInsertion ? inserted : removed).Add(editIndex);
                return new Box(newLeft, newTop, newRight, newBottom);
            }
        }
        return null;
    }
}
