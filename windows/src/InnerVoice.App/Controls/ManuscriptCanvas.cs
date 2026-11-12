using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Controls;

/// The colored manuscript: every paragraph laid out with Avalonia's TextLayout, each line's
/// color drawn behind its words (Highlight or Outline) or under them (Underline), dashed for
/// thoughts, dotted for written text, red when the narrator is needed, and the speaker's
/// initials in the left margin. Only the paragraphs on screen are drawn.
///
/// Ported from the Mac's ManuscriptTextView (TextKit 1 with a custom layout manager).
public sealed class ManuscriptCanvas : Control
{
    sealed class Block
    {
        public int Chapter;
        public Paragraph? Paragraph;
        public string Text = "";
        public bool IsHeading;
        public double Y, Height, X, Width, SpaceBefore, SpaceAfter;
        public TextLayout? Layout;
        public List<Line> Lines = [];
    }

    ProjectDocument? document;
    readonly List<Block> blocks = [];
    readonly Dictionary<Guid, Block> byParagraph = [];
    readonly List<double> chapterTops = [];
    double builtWidth = -1, totalHeight;
    bool needsLayout = true;
    Rect viewport;

    public HashSet<Guid> SelectedLines { get; } = [];
    /// Search matches, in book order, and the one Find Next is on.
    public List<(Guid Paragraph, TextSpan Range)> SearchHits { get; } = [];
    public int CurrentHit { get; set; } = -1;
    /// A paragraph to flash briefly after Show (from Review, the index, retakes).
    public Guid? FlashParagraph { get; set; }

    /// A click on a line: the line, its rectangle in this control, and the keys held.
    public event Action<Line, Rect, KeyModifiers>? LineClicked;
    /// A right-click: the line under the pointer (if any), the paragraph, and the point.
    public event Action<Line?, Paragraph?, Point>? ContextRequestedAt;
    /// A click on narration (no line).
    public event Action<Paragraph?, KeyModifiers>? NarrationClicked;
    /// A click (or right-click) on a name in the margin: the line it names and its rectangle.
    /// The name stands for its whole paragraph.
    public event Action<Line, Rect, KeyModifiers>? MarginNameClicked;

    public ManuscriptCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        EffectiveViewportChanged += (_, e) =>
        {
            viewport = e.EffectiveViewport;
            InvalidateVisual();
        };
        ActualThemeVariantChanged += (_, _) => Rebuild();
        AppSettings.Changed += Rebuild;
    }

    public ProjectDocument? Document
    {
        get => document;
        set
        {
            if (document is not null) document.Changed -= OnChanged;
            document = value;
            if (document is not null) document.Changed += OnChanged;
            Rebuild();
        }
    }

    void OnChanged(bool textChanged)
    {
        // Lines and colors change often; paragraphs only when text was deleted or undone.
        SelectedLines.IntersectWith(document?.Project.Lines.Select(l => l.Id) ?? []);
        if (textChanged) Rebuild();
        else
        {
            AttachLines();
            InvalidateVisual();
        }
    }

    public void Rebuild()
    {
        needsLayout = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // MARK: Look

    static AppSettings S => AppSettings.Current;
    double FontSize => S.ReadingFontSize;
    double LineHeight => Math.Round(FontSize * (S.LineSpacing == LineSpacing.Single ? 1.32 : 2.0) * 1.15);
    Typeface Body => new(Look.ReadingFamily(S.ReadingSerif));
    Typeface Italic => new(Look.ReadingFamily(S.ReadingSerif), FontStyle.Italic);
    Typeface Heading => new(Look.ReadingFamily(S.ReadingSerif), FontStyle.Normal, FontWeight.SemiBold);

    static readonly Typeface MarkerFace = new(Look.UIFamily, FontStyle.Normal, FontWeight.Bold);
    const double MarkerSize = 10, MarkerMaxText = 150;

    string MarkerLabel(BookCharacter c) => S.MarginNames switch
    {
        MarginNames.FirstName => c.FirstName,
        MarginNames.FullName => c.Name,
        _ => c.Initials,
    };

    /// Room left of the text for the margin labels: initials need little, full names more.
    double Gutter()
    {
        if (document is null || !document.Project.Settings.ShowInitials) return 72;
        // Room for a shading arrow too (Casting Style).
        var extra = document.Project.Settings.CastingStyle is null ? "?" : " ↓↓";
        double widest = document.Project.Characters.Select(c => MarkerWidth(MarkerLabel(c) + extra)).DefaultIfEmpty(0).Max();
        return Math.Max(72, widest + 40);
    }

    // MARK: Layout

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        if (needsLayout || Math.Abs(width - builtWidth) > 0.5) Layout(width);
        return new Size(width, totalHeight);
    }

    void Layout(double width)
    {
        needsLayout = false;
        builtWidth = width;
        foreach (var b in blocks) b.Layout?.Dispose();
        blocks.Clear();
        byParagraph.Clear();
        chapterTops.Clear();
        totalHeight = 0;
        if (document is null) return;

        double f = FontSize;
        double gutter = Gutter();
        double measure = Math.Min(Math.Max(360, width - 2 * gutter), f * 42);
        double side = Math.Max(gutter, (width - measure) / 2);
        var ink = Look.Brush(Look.Ink);
        var body = Body;
        var italic = Italic;
        double headingSize = Math.Round(f * 1.55);
        double lineHeight = LineHeight;

        double y = 40;
        var project = document.Project;
        for (int c = 0; c < project.Chapters.Count; c++)
        {
            var chapter = project.Chapters[c];
            chapterTops.Add(y);
            if (chapter.Title.Length > 0)
            {
                var hb = new Block { Chapter = c, IsHeading = true, Text = chapter.Title, X = side, Width = measure };
                hb.SpaceBefore = c == 0 ? 0 : f * 2.5;
                hb.SpaceAfter = f * 1.25;
                hb.Layout = new TextLayout(chapter.Title, Heading, headingSize, ink, TextAlignment.Left, TextWrapping.Wrap,
                    maxWidth: measure);
                y += hb.SpaceBefore;
                hb.Y = y;
                hb.Height = hb.Layout.Height;
                y += hb.Height + hb.SpaceAfter;
                blocks.Add(hb);
            }
            foreach (var p in chapter.Paragraphs)
            {
                var b = new Block { Chapter = c, Paragraph = p, Text = p.Text };
                bool quote = p.Style == ParagraphStyle.BlockQuote;
                bool sceneBreak = p.Style == ParagraphStyle.SceneBreak;
                b.X = side + (quote ? 28 : 0);
                b.Width = measure - (quote ? 28 : 0);
                b.SpaceAfter = sceneBreak ? f : f * 0.75;
                List<ValueSpan<TextRunProperties>>? overrides = null;
                foreach (var r in p.Italic)
                {
                    if (r.End > p.Text.Length || r.Length <= 0) continue;
                    overrides ??= [];
                    overrides.Add(new ValueSpan<TextRunProperties>(r.Location, r.Length,
                        new GenericTextRunProperties(italic, f, foregroundBrush: ink)));
                }
                b.Layout = new TextLayout(p.Text.Length == 0 ? " " : p.Text, body, f, ink,
                    sceneBreak ? TextAlignment.Center : TextAlignment.Left, TextWrapping.Wrap,
                    maxWidth: b.Width, lineHeight: lineHeight, textStyleOverrides: overrides);
                b.Y = y;
                b.Height = b.Layout.Height;
                y += b.Height + b.SpaceAfter;
                blocks.Add(b);
                byParagraph[p.Id] = b;
            }
        }
        totalHeight = y + 80;
        AttachLines();
    }

    void AttachLines()
    {
        if (document is null) return;
        foreach (var b in blocks) b.Lines.Clear();
        foreach (var l in document.Project.Lines)
            if (byParagraph.TryGetValue(l.ParagraphID, out var b)) b.Lines.Add(l);
        foreach (var b in blocks) b.Lines.Sort((a, c) => a.Range.Location.CompareTo(c.Range.Location));
    }

    // MARK: Drawing

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Look.Brush(Look.Paper), new Rect(Bounds.Size));
        if (document is null || blocks.Count == 0) return;
        var view = viewport.Width > 0 ? viewport : new Rect(Bounds.Size);
        double top = view.Top - 200, bottom = view.Bottom + 200;
        int first = FirstBlockAt(top);
        bool dark = Look.IsDark;
        var project = document.Project;
        var drawnMarkers = new HashSet<int>();

        for (int i = first; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Y > bottom) break;
            if (b.Layout is null) continue;
            var origin = new Point(b.X, b.Y);
            if (b.Paragraph is { } para)
            {
                if (FlashParagraph == para.Id)
                    context.FillRectangle(Look.Brush(Look.Accent.WithAlpha(dark ? 0.18 : 0.10)),
                        new Rect(b.X - 10, b.Y - 4, b.Width + 20, b.Height + 8), 6);
                DrawSearchHits(context, b, origin);
                DrawSelection(context, i, b, origin);
                foreach (var line in b.Lines) DrawLine(context, project, b, line, origin, dark);
            }
            if (b.IsHeading) DrawSelection(context, i, b, origin);
            b.Layout.Draw(context, origin);
            if (b.Paragraph is not null && (project.Settings.ShowInitials || b.Lines.Any(project.NeedsYou)))
                DrawMarkers(context, project, b, origin, dark, drawnMarkers);
        }
    }

    int FirstBlockAt(double y)
    {
        int lo = 0, hi = blocks.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (blocks[mid].Y + blocks[mid].Height < y) { lo = mid + 1; }
            else { found = mid; hi = mid - 1; }
        }
        return found;
    }

    IEnumerable<Rect> RangeRects(Block b, TextSpan range, Point origin)
    {
        if (b.Layout is null) yield break;
        int start = Math.Clamp(range.Location, 0, b.Text.Length);
        int length = Math.Clamp(range.Length, 0, b.Text.Length - start);
        if (length == 0) yield break;
        foreach (var r in b.Layout.HitTestTextRange(start, length))
        {
            if (r.Width <= 0.5) continue;
            yield return r.Translate(new Vector(origin.X, origin.Y));
        }
    }

    /// The box around the words on one visual line: the text's own height, not the line's.
    Rect TextBox(Rect lineRect)
    {
        double h = Math.Min(lineRect.Height, FontSize * 1.38);
        return new Rect(lineRect.X, lineRect.Y + (lineRect.Height - h) / 2, lineRect.Width, h);
    }

    void DrawLine(DrawingContext context, Project project, Block b, Line line, Point origin, bool dark)
    {
        bool needs = project.NeedsYou(line);
        var speaker = project.Character(line.SpeakerID);
        var color = needs ? Look.Attention : Look.Hex(speaker?.ColorHex);
        var style = project.Settings.Style;
        bool selected = SelectedLines.Contains(line.Id);
        foreach (var rect in RangeRects(b, line.Range, origin))
        {
            var box = TextBox(rect).Inflate(new Thickness(3, -0.5));
            if (style == ColorStyle.Underline)
            {
                double y = box.Bottom - 1;
                var pen = new Pen(Look.Brush(color.WithAlpha(dark ? 0.95 : 0.85)), 2.5, Dashes(line.Kind, 2.5, underline: true), PenLineCap.Round);
                context.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
            }
            else
            {
                var rr = new RoundedRect(box, 4);
                if (style == ColorStyle.Highlight && line.Kind == LineKind.Spoken)
                {
                    context.DrawRectangle(Look.Brush(color.WithAlpha(dark ? 0.34 : 0.24)), null, rr);
                }
                else
                {
                    var pen = new Pen(Look.Brush(color.WithAlpha(0.9)), 1.5, Dashes(line.Kind, 1.5, underline: false));
                    context.DrawRectangle(Look.Brush(color.WithAlpha(dark ? 0.10 : 0.06)), pen, rr);
                }
            }
            if (selected)
                context.DrawRectangle(null, new Pen(Look.Brush(Look.Accent), 2), new RoundedRect(box.Inflate(2), 6));
        }
    }

    /// Dashed for thoughts, dotted for written text. Avalonia's dashes are in pen widths.
    static DashStyle? Dashes(LineKind kind, double width, bool underline) => kind switch
    {
        LineKind.Thought => underline ? new DashStyle([6 / width, 4 / width], 0) : new DashStyle([4 / width, 3 / width], 0),
        LineKind.Written => underline ? new DashStyle([0.1 / width, 4.5 / width], 0) : new DashStyle([1.5 / width, 2.5 / width], 0),
        _ => null,
    };

    void DrawSearchHits(DrawingContext context, Block b, Point origin)
    {
        if (SearchHits.Count == 0 || b.Paragraph is null) return;
        for (int i = 0; i < SearchHits.Count; i++)
        {
            var (pid, range) = SearchHits[i];
            if (pid != b.Paragraph.Id) continue;
            var color = i == CurrentHit ? Color.Parse("#FACC15") : Color.Parse("#FDE68A");
            foreach (var r in RangeRects(b, range, origin))
                context.DrawRectangle(Look.Brush(color.WithAlpha(i == CurrentHit ? 0.9 : 0.55)), null, new RoundedRect(TextBox(r).Inflate(new Thickness(1, 0)), 3));
        }
    }

    void DrawMarkers(DrawingContext context, Project project, Block b, Point origin, bool dark, HashSet<int> drawn)
    {
        if (b.Layout is null) return;
        foreach (var line in b.Lines)
        {
            bool needs = project.NeedsYou(line);
            if (!project.Settings.ShowInitials && !needs) continue;
            int start = Math.Clamp(line.Range.Location, 0, b.Text.Length);
            var first = b.Layout.HitTestTextPosition(start).Translate(new Vector(origin.X, origin.Y));
            int y = (int)first.Top;
            if (!drawn.Add(y)) continue;
            var speaker = project.Character(line.SpeakerID);
            var color = needs ? Look.Attention : Look.Hex(speaker?.ColorHex);
            var named = speaker is null ? null : MarkerLabel(speaker);
            var label = needs ? (named is null ? "?" : named + "?") : named ?? "";
            // Casting Style: an arrow for how to shade them (↓ lower, ↑ higher, doubled for more).
            if (!needs && speaker is not null && document is not null && project.Settings.CastingStyle is not null
                && document.Shades().TryGetValue(speaker.Id, out var shade) && shade.Arrow is { } arrow) label += " " + arrow;
            var box = TextBox(new Rect(first.X, first.Y, 1, first.Height));
            DrawMarker(context, label, color, b.X - (b.Paragraph?.Style == ParagraphStyle.BlockQuote ? 28 : 0) - 18, box.Center.Y, dark);
        }
    }

    static FormattedText MarkerText(string s, IBrush brush) =>
        new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, MarkerFace, MarkerSize, brush);

    /// The label as drawn: short enough to fit the margin.
    static string Fit(string text)
    {
        if (MarkerText(text, Brushes.Black).Width <= MarkerMaxText) return text;
        var cut = text;
        while (cut.Length > 1 && MarkerText(cut + "…", Brushes.Black).Width > MarkerMaxText) cut = cut[..^1];
        return cut.Trim() + "…";
    }

    static double MarkerWidth(string text) => Math.Max(22, Math.Ceiling(MarkerText(Fit(text), Brushes.Black).Width) + 10);

    /// A pill with the speaker's label, its right edge just left of the text.
    static void DrawMarker(DrawingContext context, string raw, Color color, double rightEdge, double midY, bool dark)
    {
        if (raw.Length == 0) return;
        var text = Fit(raw);
        var ink = dark ? color.Blend(Colors.White, 0.35) : color.Blend(Colors.Black, 0.25);
        var ft = MarkerText(text, Look.Brush(ink));
        double w = MarkerWidth(raw);
        var box = new Rect(rightEdge - w, midY - 9, w, 18);
        context.DrawRectangle(Look.Brush(color.WithAlpha(dark ? 0.28 : 0.16)), null, new RoundedRect(box, 9));
        context.DrawText(ft, new Point(box.Center.X - ft.Width / 2, box.Center.Y - ft.Height / 2));
    }

    // MARK: Hit testing

    (Block? Block, int Position, bool Inside) HitTest(Point p)
    {
        if (blocks.Count == 0) return (null, -1, false);
        int i = FirstBlockAt(p.Y);
        var b = blocks[Math.Min(i, blocks.Count - 1)];
        if (b.Layout is null || p.Y < b.Y - b.SpaceBefore || p.Y > b.Y + b.Height + b.SpaceAfter) return (b, -1, false);
        var local = new Point(p.X - b.X, p.Y - b.Y);
        var hit = b.Layout.HitTestPoint(local);
        return (b, hit.TextPosition, hit.IsInside);
    }

    public Line? LineAt(Point p)
    {
        var (b, pos, inside) = HitTest(p);
        if (b?.Paragraph is null || pos < 0 || !inside) return null;
        return b.Lines.FirstOrDefault(l => pos >= l.Range.Location && pos < l.Range.End);
    }

    /// The line whose name is drawn in the margin at `p`, if any.
    public Line? MarginLineAt(Point p)
    {
        if (blocks.Count == 0 || document is null) return null;
        var b = blocks[Math.Min(FirstBlockAt(p.Y), blocks.Count - 1)];
        if (b.Layout is null || b.Paragraph is null || p.X >= b.X - 4) return null;
        var project = document.Project;
        foreach (var line in b.Lines)
        {
            if (!project.Settings.ShowInitials && !project.NeedsYou(line)) continue;
            var first = b.Layout.HitTestTextPosition(Math.Clamp(line.Range.Location, 0, b.Text.Length)).Translate(new Vector(b.X, b.Y));
            if (p.Y >= first.Top && p.Y <= first.Bottom) return line;
        }
        return null;
    }

    /// Where a line's name sits in the margin, in this control (for tests).
    public Point? MarginPointOf(Line line)
    {
        if (!byParagraph.TryGetValue(line.ParagraphID, out var b) || b.Layout is null) return null;
        var first = b.Layout.HitTestTextPosition(Math.Clamp(line.Range.Location, 0, b.Text.Length)).Translate(new Vector(b.X, b.Y));
        return new Point(b.X - 24, first.Center.Y);
    }

    /// The paragraph a line belongs to, for keyboard navigation.
    public void ScrollToLine(Line line) => ScrollToParagraph(line.ParagraphID);

    /// The rectangle of a line's first visual line, in this control.
    public Rect? RectOf(Line line)
    {
        if (!byParagraph.TryGetValue(line.ParagraphID, out var b)) return null;
        var rects = RangeRects(b, line.Range, new Point(b.X, b.Y)).ToList();
        if (rects.Count == 0) return null;
        return rects.Aggregate((a, c) => a.Union(c));
    }

    // MARK: Pointer and selection

    (int Block, int Pos)? anchor, focus;
    Point pressPoint;
    Line? pressedLine, pressedMargin;
    bool dragging;

    /// The selected text, as ranges inside paragraphs and chapter headings, in book order.
    public List<(Paragraph? Paragraph, int Chapter, TextSpan Range)> SelectedText()
    {
        var output = new List<(Paragraph?, int, TextSpan)>();
        if (anchor is not { } a || focus is not { } f) return output;
        var (s, e) = a.Block < f.Block || (a.Block == f.Block && a.Pos <= f.Pos) ? (a, f) : (f, a);
        for (int i = s.Block; i <= e.Block && i < blocks.Count; i++)
        {
            var b = blocks[i];
            int from = i == s.Block ? s.Pos : 0;
            int to = i == e.Block ? e.Pos : b.Text.Length;
            if (to > from) output.Add((b.Paragraph, b.Chapter, new TextSpan(from, to - from)));
        }
        return output;
    }

    public bool HasSelection => SelectedText().Count > 0;

    public void ClearSelection()
    {
        anchor = focus = null;
        InvalidateVisual();
    }

    (int Block, int Pos)? Caret(Point p)
    {
        if (blocks.Count == 0) return null;
        int i = Math.Min(FirstBlockAt(p.Y), blocks.Count - 1);
        var b = blocks[i];
        if (b.Layout is null) return null;
        if (p.Y < b.Y) return (i, 0);
        var hit = b.Layout.HitTestPoint(new Point(p.X - b.X, p.Y - b.Y));
        int pos = Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, b.Text.Length);
        if (p.Y > b.Y + b.Height) pos = b.Text.Length;
        return (i, pos);
    }

    void DrawSelection(DrawingContext context, int blockIndex, Block b, Point origin)
    {
        foreach (var (para, chapter, range) in SelectedText())
        {
            bool match = b.Paragraph is not null ? para?.Id == b.Paragraph.Id : (para is null && chapter == b.Chapter && b.IsHeading);
            if (!match) continue;
            foreach (var r in RangeRects(b, range, origin))
                context.FillRectangle(Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.45 : 0.25)), r);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var line = LineAt(point);
        var (b, _, _) = HitTest(point);
        var margin = line is null ? MarginLineAt(point) : null;
        if (props.IsRightButtonPressed)
        {
            if (margin is not null && RectOf(margin) is { } mr) MarginNameClicked?.Invoke(margin, mr, KeyModifiers.None);
            else ContextRequestedAt?.Invoke(line, b?.Paragraph, point);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        pressPoint = point;
        pressedLine = line;
        pressedMargin = margin;
        dragging = false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && anchor is not null && line is null)
        {
            focus = Caret(point);
            dragging = true;
        }
        else
        {
            anchor = Caret(point);
            focus = anchor;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (anchor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        if (!dragging && Math.Abs(point.X - pressPoint.X) + Math.Abs(point.Y - pressPoint.Y) < 5) return;
        dragging = true;
        focus = Caret(point);
        InvalidateVisual();
        // Keep the drag in view near the edges.
        if (Scroller is { } sv)
        {
            var inView = e.GetPosition(sv);
            if (inView.Y < 20) sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y - 20));
            else if (inView.Y > sv.Viewport.Height - 20) sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y + 20);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (dragging) { dragging = false; return; }
        anchor = focus = null;
        var point = e.GetPosition(this);
        if (pressedLine is not null && RectOf(pressedLine) is { } rect) LineClicked?.Invoke(pressedLine, rect, e.KeyModifiers);
        else if (pressedMargin is not null && RectOf(pressedMargin) is { } mr) MarginNameClicked?.Invoke(pressedMargin, mr, e.KeyModifiers);
        else
        {
            var (b, _, _) = HitTest(point);
            NarrationClicked?.Invoke(b?.Paragraph, e.KeyModifiers);
        }
        pressedLine = pressedMargin = null;
        InvalidateVisual();
    }

    // MARK: Scrolling

    ScrollViewer? Scroller => Parent as ScrollViewer ?? this.FindAncestorOfType<ScrollViewer>();

    public void ScrollToParagraph(Guid id, bool flash = false)
    {
        if (needsLayout) Measure(new Size(Bounds.Width > 0 ? Bounds.Width : 900, double.PositiveInfinity));
        if (!byParagraph.TryGetValue(id, out var b) || Scroller is not { } sv) return;
        double target = Math.Max(0, b.Y - sv.Viewport.Height / 3);
        sv.Offset = new Vector(sv.Offset.X, target);
        if (flash)
        {
            FlashParagraph = id;
            InvalidateVisual();
            _ = Task.Delay(1400).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (FlashParagraph == id) { FlashParagraph = null; InvalidateVisual(); }
            }));
        }
    }

    public void ScrollToChapter(int chapter)
    {
        if (chapter < 0 || chapter >= chapterTops.Count || Scroller is not { } sv) return;
        sv.Offset = new Vector(sv.Offset.X, Math.Max(0, chapterTops[chapter] - 20));
    }

    /// The chapter at the top of the window, for the sidebar.
    public int VisibleChapter
    {
        get
        {
            if (blocks.Count == 0) return 0;
            var view = viewport.Width > 0 ? viewport : new Rect(Bounds.Size);
            return blocks[FirstBlockAt(view.Top + 60)].Chapter;
        }
    }

    public void Detach()
    {
        AppSettings.Changed -= Rebuild;
        Document = null;
    }
}

static class VisualExtensions
{
    public static T? FindAncestorOfType<T>(this Visual v) where T : class
    {
        var p = v.GetVisualParentSafe();
        while (p is not null)
        {
            if (p is T t) return t;
            p = p.GetVisualParentSafe();
        }
        return null;
    }

    static Visual? GetVisualParentSafe(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
}
