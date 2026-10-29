using System.Globalization;
using System.Windows;
using System.Windows.Media;
using JizuraLive.Pv;
using JizuraLive.Vts;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage.Timeline;

public sealed partial class TimelineView
{
    private const double ValuePad = 3;
    private const double KeyRadius = 4;

    private static readonly Brush BgBrush = Solid(0x1E, 0x1E, 0x1E);
    private static readonly Brush PanelBrush = Solid(0x25, 0x25, 0x25);
    private static readonly Brush RowBrush = Solid(0x23, 0x23, 0x23);
    private static readonly Brush RowAltBrush = Solid(0x1F, 0x1F, 0x1F);
    private static readonly Brush HeaderBrush = Solid(0x2A, 0x2A, 0x2A);
    private static readonly Brush GroupHeaderBrush = Solid(0x30, 0x30, 0x30);
    private static readonly Brush GroupSelectedBrush = Solid(0x2C, 0x3A, 0x50);
    private static readonly Brush GroupRowBrush = Solid(0x20, 0x20, 0x20);
    private static readonly Brush GroupSelectedRowBrush = Solid(0x24, 0x2E, 0x3E);
    private static readonly Brush SummaryKeyBrush = Solid(0x8A, 0x9A, 0xB0);
    private static readonly Brush SummarySegmentBrush = Solid(0x44, 0x52, 0x68);
    private static readonly Brush TwirlBrush = Solid(0xB4, 0xB4, 0xB4);
    private static readonly Brush AccentBrush = Solid(0x4C, 0x9A, 0xFF);
    private static readonly Brush AccentSoftBrush = Solid(0x2B, 0x3E, 0x5C);
    private static readonly Brush WaveBrush = Solid(0x59, 0xC2, 0xA0);
    private static readonly Brush FgBrush = Solid(0xE6, 0xE6, 0xE6);
    private static readonly Brush DimBrush = Solid(0x9E, 0x9E, 0x9E);
    private static readonly Brush FaintBrush = Solid(0x6B, 0x6B, 0x6B);
    private static readonly Brush PlayheadBrush = Solid(0xFF, 0x5F, 0x6E);
    private static readonly Brush ConflictBrush = Solid(0xFF, 0x5F, 0x6E);
    private static readonly Brush ScrollTrackBrush = Solid(0x3A, 0x3A, 0x3A);
    private static readonly Brush ScrollThumbBrush = Solid(0x9E, 0x9E, 0x9E, 0xFF);
    private static readonly Brush ScrollThumbHotBrush = Solid(0xC8, 0xC8, 0xC8, 0xFF);
    private static readonly Brush HotkeyCueBrush = Solid(0xE8, 0xB4, 0x4A);
    private static readonly Brush ExpressionCueBrush = Solid(0x5E, 0xD6, 0x9A);
    private static readonly Brush TintCueBrush = Solid(0xE8, 0x6E, 0xC8);
    private static readonly Brush HotkeyCueFill = Solid(0x55, 0xE8, 0xB4, 0x4A);
    private static readonly Brush ExpressionCueFill = Solid(0x55, 0x5E, 0xD6, 0x9A);
    private static readonly Brush TintCueFill = Solid(0x55, 0xE8, 0x6E, 0xC8);
    private static readonly Brush SectionBrush = Solid(0x14, 0x5B, 0x8C, 0xFF);
    private static readonly Pen BeatPen = FrozenPen(Solid(0x3A, 0x3A, 0x3A), 1);
    private static readonly Brush EyeBrush = Solid(0x9E, 0x9E, 0x9E);
    private static readonly Pen EyePen = FrozenPen(Solid(0x9E, 0x9E, 0x9E), 1);
    private static readonly Pen EyeOffPen = FrozenPen(Solid(0xE8, 0xB4, 0x4A), 1);
    private static readonly Brush TipBrush = Solid(0xE6, 0x24, 0x24, 0x24);
    private static readonly Brush HoverRowBrush = Solid(0x12, 0xE6, 0xE6, 0xE6);
    private static readonly Pen HoverLinePen = FrozenPen(DimBrush, 1);
    private static readonly Pen LinePen = FrozenPen(Solid(0x33, 0x33, 0x33), 1);
    private static readonly Pen SoftPen = FrozenPen(Solid(0x19, 0x19, 0x20), 1);
    private static readonly Pen WavePen = FrozenPen(WaveBrush, 1);
    private static readonly Pen CurvePen = FrozenPen(AccentBrush, 1.4);
    private static readonly Pen ZeroPen = FrozenPen(Solid(0x30, 0x30, 0x3C), 1);
    private static readonly Pen KeyPen = FrozenPen(AccentBrush, 1);
    private static readonly Pen SelKeyPen = FrozenPen(FgBrush, 1.4);
    private static readonly Pen PlayheadPen = FrozenPen(PlayheadBrush, 1.5);
    private static readonly Typeface UiFont = new("Microsoft YaHei UI");
    private static readonly Typeface MonoFont = new("Consolas");

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        double contentW = ContentWidth, contentH = ContentHeight;
        FitOnce(contentW);

        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        dc.DrawRectangle(PanelBrush, null, new Rect(0, 0, HeaderWidth, h));

        var rows = BuildRows();
        var contentRect = new Rect(HeaderWidth, 0, contentW, contentH);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, HeaderWidth, h)));
        foreach (var row in rows)
        {
            if (row.Kind is not (RowKind.Ruler or RowKind.Waveform or RowKind.Lyrics)) continue;
            if (row.Top + row.Height <= 0) continue;
            if (row.Top >= contentH) break;
            RenderHeader(dc, row);
        }
        dc.Pop();

        dc.PushClip(new RectangleGeometry(
            new Rect(0, _trackTop, HeaderWidth, Math.Max(0, h - _trackTop))));
        foreach (var row in rows)
        {
            if (row.Kind is RowKind.Ruler or RowKind.Waveform or RowKind.Lyrics) continue;
            if (row.Top + row.Height <= _trackTop) continue;
            if (row.Top >= contentH) break;
            RenderHeader(dc, row);
        }
        dc.Pop();

        dc.PushClip(new RectangleGeometry(contentRect));
        foreach (var row in rows)
        {
            if (row.Kind is not (RowKind.Ruler or RowKind.Waveform or RowKind.Lyrics)) continue;
            if (row.Top + row.Height <= 0) continue;
            if (row.Top >= contentH) break;
            switch (row.Kind)
            {
                case RowKind.Ruler: RenderRuler(dc, row); break;
                case RowKind.Waveform: RenderWaveform(dc, row); break;
                case RowKind.Lyrics: RenderLyrics(dc, row); break;
            }
        }
        dc.Pop();

        dc.PushClip(new RectangleGeometry(
            new Rect(HeaderWidth, _trackTop, contentW, Math.Max(0, contentH - _trackTop))));
        foreach (var row in rows)
        {
            if (row.Kind is RowKind.Ruler or RowKind.Waveform or RowKind.Lyrics) continue;
            if (row.Top + row.Height <= _trackTop) continue;
            if (row.Top >= contentH) break;
            switch (row.Kind)
            {
                case RowKind.Group: RenderGroup(dc, row); break;
                case RowKind.Design: RenderDesignTrack(dc, row); break;
                case RowKind.Automation: RenderAutomation(dc, row); break;
                case RowKind.VtsCue: RenderVtsCueTrack(dc, row); break;
            }
        }
        dc.Pop();

        RenderHover(dc, rows, contentW, contentH, w);

        dc.PushClip(new RectangleGeometry(contentRect));
        RenderPlayhead(dc, contentH);
        dc.Pop();

        dc.DrawLine(LinePen, new Point(HeaderWidth, 0), new Point(HeaderWidth, h));
        RenderScrollbar(dc, w, h);
        RenderVerticalScrollbar(dc, w);
    }

    private void RenderVerticalScrollbar(DrawingContext dc, double w)
    {
        if (!VerticalScrollbar(out var trackTop, out var trackHeight, out var thumbTop, out var thumbHeight)) return;

        dc.DrawRectangle(ScrollTrackBrush, null, new Rect(w - ScrollbarWidth, trackTop, ScrollbarWidth, trackHeight));
        var thumbBrush = _drag == DragMode.VPan ? ScrollThumbHotBrush : ScrollThumbBrush;
        dc.DrawRoundedRectangle(thumbBrush, null,
            new Rect(w - ScrollbarWidth + 2, thumbTop, ScrollbarWidth - 4, thumbHeight), 4, 4);
    }

    private void RenderHover(DrawingContext dc, List<Row> rows, double contentW, double contentH, double w)
    {
        Row? hover = null;
        if (_hoverActive && _drag == DragMode.None && _hoverY >= 0 && _hoverY < contentH)
        {
            foreach (var candidate in rows)
            {
                if (candidate.Top >= contentH) break;
                if (_hoverY >= candidate.Top && _hoverY < candidate.Top + candidate.Height)
                {
                    hover = candidate;
                    break;
                }
            }
        }

        if (hover is { } row)
        {
            _hoverRowTop = row.Top;
            _hoverRowBottom = row.Top + row.Height;
            _hoverRowIsRuler = row.Kind == RowKind.Ruler;
        }
        else
        {
            _hoverRowTop = double.NaN;
            _hoverRowBottom = double.NaN;
            _hoverRowIsRuler = false;
        }

        if (hover is not { } hovered) return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, contentH)));
        dc.DrawRectangle(HoverRowBrush, null, new Rect(0, hovered.Top, w, hovered.Height));

        if (hovered.Kind == RowKind.Ruler
            && _hoverX >= HeaderWidth && _hoverX <= HeaderWidth + contentW)
        {
            dc.DrawLine(HoverLinePen, new Point(_hoverX, hovered.Top),
                new Point(_hoverX, hovered.Top + hovered.Height));

            var label = Text(FormatTime(TimeAt(_hoverX - HeaderWidth)), 10, FgBrush, MonoFont);
            double left = _hoverX + 5;
            if (left + label.Width + 8 > HeaderWidth + contentW)
                left = Math.Max(HeaderWidth, _hoverX - 5 - label.Width - 8);
            var box = new Rect(left, hovered.Top + 2, label.Width + 8, 16);
            dc.DrawRoundedRectangle(TipBrush, null, box, 3, 3);
            dc.DrawText(label, new Point(box.X + 4, box.Y + 1));
        }

        dc.Pop();
    }

    private void RenderRuler(DrawingContext dc, Row row)
    {
        double w = ContentWidth;
        dc.DrawRectangle(HeaderBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));

        double step = TimelineMath.RulerStep(_pixelsPerSecond);
        double end = _viewStart + w / _pixelsPerSecond;
        if (_snap is not null)
        {
            foreach (var section in _snap.Sections)
            {
                if (section.End < _viewStart || section.Start > end) continue;
                double sx0 = HeaderWidth + TimelineMath.TimeToX(
                    Math.Max(section.Start, _viewStart), _viewStart, _pixelsPerSecond);
                double sx1 = HeaderWidth + TimelineMath.TimeToX(
                    Math.Min(section.End, end), _viewStart, _pixelsPerSecond);
                dc.DrawRectangle(SectionBrush, null,
                    new Rect(sx0, row.Top, Math.Max(1, sx1 - sx0), 3));
            }

            foreach (var beat in _snap.Beats)
            {
                if (beat < _viewStart || beat > end) continue;
                double bx = HeaderWidth + TimelineMath.TimeToX(beat, _viewStart, _pixelsPerSecond);
                dc.DrawLine(BeatPen, new Point(bx, row.Top + row.Height - 5),
                    new Point(bx, row.Top + row.Height));
            }
        }

        for (long i = (long)Math.Ceiling(_viewStart / step); ; i++)
        {
            double t = i * step;
            if (t > end) break;
            double x = HeaderWidth + TimelineMath.TimeToX(t, _viewStart, _pixelsPerSecond);
            dc.DrawLine(LinePen, new Point(x, row.Top + row.Height - 7), new Point(x, row.Top + row.Height));
            dc.DrawText(Text(FormatTime(t), 10, FaintBrush, MonoFont), new Point(x + 3, row.Top + 4));
        }
        dc.DrawLine(LinePen, new Point(HeaderWidth, row.Top + row.Height), new Point(HeaderWidth + w, row.Top + row.Height));
    }

    private void RenderWaveform(DrawingContext dc, Row row)
    {
        double w = ContentWidth;
        dc.DrawRectangle(RowBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));

        double mid = row.Top + row.Height / 2;
        double half = (row.Height - 4) / 2;
        dc.DrawLine(SoftPen, new Point(HeaderWidth, mid), new Point(HeaderWidth + w, mid));

        if (_snap?.Waveform is not { } wf || _snap.DurationSeconds <= 0 || wf.SampleCount <= 0) return;

        long first = TimelineMath.TimeToSample(_viewStart, _snap.DurationSeconds, wf.SampleCount);
        long last = TimelineMath.TimeToSample(_viewStart + w / _pixelsPerSecond, _snap.DurationSeconds, wf.SampleCount);
        long span = Math.Max(1, last - first);
        int cols = (int)Math.Ceiling(w);
        int level = wf.SelectLevel(first, span, cols);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (int i = 0; i < cols; i++)
            {
                long s0 = first + (long)((double)span * i / cols);
                long s1 = first + (long)((double)span * (i + 1) / cols);
                long count = Math.Max(1, s1 - s0);
                float min = float.PositiveInfinity, max = float.NegativeInfinity;
                for (int c = 0; c < wf.Channels; c++)
                {
                    if (!wf.TryRead(level, c, s0, count, out float lo, out float hi)) continue;
                    min = Math.Min(min, lo);
                    max = Math.Max(max, hi);
                }
                if (max < min) continue;
                double x = HeaderWidth + i + 0.5;
                g.BeginFigure(new Point(x, mid - max * half), false, false);
                g.LineTo(new Point(x, mid - min * half), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, WavePen, geometry);
    }

    private static readonly Brush SegUserBrush = Solid(0x3D, 0x55, 0x9E);
    private static readonly Brush SegAutoBrush = Solid(0x27, 0x34, 0x5C);
    private static readonly Brush SegUserSelectedBrush = Solid(0x5B, 0x8C, 0xFF);
    private static readonly Brush SegAutoSelectedBrush = Solid(0x3A, 0x50, 0x8F);
    private static readonly Brush SegLockedPenBrush = Solid(0xE8, 0xB4, 0x4A);
    private static readonly Brush DesignEmptyBrush = Solid(0x0F, 0x0F, 0x14);

    private void RenderDesignTrack(DrawingContext dc, Row row)
    {
        double w = ContentWidth;
        dc.DrawRectangle(DesignEmptyBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));
        dc.DrawLine(SoftPen, new Point(HeaderWidth, row.Top), new Point(HeaderWidth + w, row.Top));
        if (row.Index < 0 || _snap is null || row.Index >= _snap.DesignTracks.Count) return;

        var track = _snap.DesignTracks[row.Index];
        bool hasSegments = track.Segments.Count > 0;
        if (hasSegments)
            dc.DrawRectangle(row.Index % 2 == 0 ? RowBrush : RowAltBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));
        if (!hasSegments) return;

        var pen = FrozenPen(SegLockedPenBrush, 1);
        double viewEnd = _viewStart + w / _pixelsPerSecond;
        foreach (var seg in track.Segments)
        {
            if (seg.End < _viewStart || seg.Start > viewEnd) continue;
            double x0 = HeaderWidth + TimelineMath.TimeToX(seg.Start, _viewStart, _pixelsPerSecond);
            double x1 = HeaderWidth + TimelineMath.TimeToX(seg.End, _viewStart, _pixelsPerSecond);
            double width = Math.Max(2, x1 - x0 - 2);
            var rect = new Rect(x0, row.Top + 3, width, row.Height - 6);

            bool selected = _selSegment is { } sel && sel.Track == row.Index
                            && string.Equals(sel.RefId, seg.RefId, StringComparison.Ordinal);
            bool auto = !string.Equals(seg.Source, "user", StringComparison.OrdinalIgnoreCase);
            var fill = auto
                ? (selected ? SegAutoSelectedBrush : SegAutoBrush)
                : (selected ? SegUserSelectedBrush : SegUserBrush);
            dc.DrawRoundedRectangle(fill, null, rect, 3, 3);

            if (selected)
            {
                dc.DrawRectangle(AccentBrush, null, new Rect(rect.X, rect.Y, 2, rect.Height));
                dc.DrawRectangle(AccentBrush, null, new Rect(rect.Right - 2, rect.Y, 2, rect.Height));
            }

            if (seg.FadeIn > 0 || seg.FadeOut > 0)
            {
                var fade = new StreamGeometry();
                using (var g = fade.Open())
                {
                    g.BeginFigure(new Point(rect.X, rect.Y + rect.Height), true, true);
                    if (seg.FadeIn > 0)
                    {
                        double fx = Math.Min(rect.Width, TimelineMath.TimeToX(seg.FadeIn, 0, _pixelsPerSecond));
                        g.LineTo(new Point(rect.X + fx, rect.Y), true, false);
                    }
                    else g.LineTo(new Point(rect.X, rect.Y), true, false);

                    if (seg.FadeOut > 0)
                    {
                        double fx = Math.Min(rect.Width, TimelineMath.TimeToX(seg.FadeOut, 0, _pixelsPerSecond));
                        g.LineTo(new Point(rect.Right - fx, rect.Y), true, false);
                    }
                    else g.LineTo(new Point(rect.Right, rect.Y), true, false);
                }
                fade.Freeze();
                dc.DrawGeometry(SegAutoBrush, null, fade);
            }

            if (seg.Locked)
            {
                const double lockH = 5;
                var lockRect = new Rect(rect.X + 3, rect.Y + 2, 4, lockH);
                dc.DrawRectangle(null, pen, lockRect);
                dc.DrawRectangle(pen.Brush, null, new Rect(rect.X + 4.5, rect.Y + lockH + 1, 1, lockH - 1));
            }

            if (rect.Width >= 24)
            {
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawText(Text(string.IsNullOrWhiteSpace(seg.Label) ? seg.Technique : seg.Label, 10,
                        selected ? BgBrush : DimBrush),
                    new Point(rect.X + 4, rect.Y + (rect.Height - 13) / 2));
                dc.Pop();
            }
        }
    }

    private void RenderLyrics(DrawingContext dc, Row row)
    {
        double w = ContentWidth;
        dc.DrawRectangle(RowAltBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));
        if (_snap is null) return;

        double end = _viewStart + w / _pixelsPerSecond;
        for (int i = 0; i < _snap.Lyrics.Count; i++)
        {
            var clip = _snap.Lyrics[i];
            if (clip.End < _viewStart || clip.Start > end) continue;
            double x0 = HeaderWidth + TimelineMath.TimeToX(clip.Start, _viewStart, _pixelsPerSecond);
            double x1 = HeaderWidth + TimelineMath.TimeToX(clip.End, _viewStart, _pixelsPerSecond);
            var rect = new Rect(x0, row.Top + 3, Math.Max(2, x1 - x0 - 2), row.Height - 6);
            bool current = _snap.PlayheadSeconds >= clip.Start && _snap.PlayheadSeconds < clip.End;
            bool conflict = _clipConflict && i == _clipIndex;
            dc.DrawRoundedRectangle(conflict ? ConflictBrush : current ? AccentBrush : AccentSoftBrush, null, rect, 3, 3);
            if (rect.Width < 24) continue;
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawText(Text(FirstLine(clip.Text), 11, current && !conflict ? BgBrush : FgBrush),
                new Point(rect.X + 5, rect.Y + 4));
            dc.Pop();
        }
    }

    private void RenderAutomation(DrawingContext dc, Row row)
    {
        double w = ContentWidth;
        var curve = _snap!.Curves[row.Index];
        dc.DrawRectangle(row.Index % 2 == 0 ? RowBrush : RowAltBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));
        dc.DrawLine(SoftPen, new Point(HeaderWidth, row.Top), new Point(HeaderWidth + w, row.Top));

        double top = row.Top + ValuePad;
        double height = row.Height - 2 * ValuePad;
        double fallback = curve.Keys.Count == 0 && curve.BaseValue is { } baseValue
            ? baseValue
            : (curve.Min + curve.Max) / 2;

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, row.Top, w, row.Height)));

        if (curve.Min <= 0 && curve.Max >= 0)
        {
            double zeroY = TimelineMath.ValueToY(0, curve.Min, curve.Max, top, height);
            dc.DrawLine(ZeroPen, new Point(HeaderWidth, zeroY), new Point(HeaderWidth + w, zeroY));
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool started = false;
            const double Step = 2.0;
            for (double x = 0; x <= w + Step * 0.5; x += Step)
            {
                var curX = Math.Min(x, w);
                double v = TimelineMath.Evaluate(curve.Keys, TimeAt(curX), fallback);
                var point = new Point(HeaderWidth + curX, TimelineMath.ValueToY(v, curve.Min, curve.Max, top, height));
                if (!started)
                {
                    g.BeginFigure(point, false, false);
                    started = true;
                }
                else g.LineTo(point, true, false);
                if (curX >= w) break;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, CurvePen, geometry);

        double viewEnd = _viewStart + w / _pixelsPerSecond;
        for (int i = 0; i < curve.Keys.Count; i++)
        {
            var key = curve.Keys[i];
            if (key.TimeSeconds < _viewStart - 0.5 || key.TimeSeconds > viewEnd + 0.5) continue;
            double x = HeaderWidth + TimelineMath.TimeToX(key.TimeSeconds, _viewStart, _pixelsPerSecond);
            double y = TimelineMath.ValueToY(key.Value, curve.Min, curve.Max, top, height);
            bool selected = IsKeySelected(curve.Address, i, key.TimeSeconds);
            dc.DrawGeometry(selected ? AccentBrush : PanelBrush, selected ? SelKeyPen : KeyPen, Diamond(x, y, KeyRadius));
        }

        dc.Pop();
    }

    private static Brush CueBrush(VtsCueKind kind) => kind switch
    {
        VtsCueKind.Hotkey => HotkeyCueBrush,
        VtsCueKind.Tint => TintCueBrush,
        _ => ExpressionCueBrush,
    };

    private static Brush CueFill(VtsCueKind kind) => kind switch
    {
        VtsCueKind.Hotkey => HotkeyCueFill,
        VtsCueKind.Tint => TintCueFill,
        _ => ExpressionCueFill,
    };

    private static string VtsCueTrackLabel(TVtsCueTrack track) =>
        string.IsNullOrEmpty(track.Label) ? CueTrackLabel(track.Kind) : track.Label;

    private static string CueTrackLabel(VtsCueKind kind) => kind switch
    {
        VtsCueKind.Hotkey => L.T("动作键"),
        VtsCueKind.Expression => L.T("表情"),
        VtsCueKind.Tint => L.T("染色"),
        _ => kind.ToString(),
    };

    private void RenderVtsCueTrack(DrawingContext dc, Row row)
    {
        var track = _snap!.VtsCueTracks[row.Index];
        double w = ContentWidth;
        dc.DrawRectangle(row.Index % 2 == 0 ? RowBrush : RowAltBrush, null,
            new Rect(HeaderWidth, row.Top, w, row.Height));
        dc.DrawLine(SoftPen, new Point(HeaderWidth, row.Top), new Point(HeaderWidth + w, row.Top));

        var brush = CueBrush(track.Kind);
        var fill = CueFill(track.Kind);
        double cy = row.Top + row.Height / 2;
        double viewEnd = _viewStart + w / _pixelsPerSecond;

        foreach (var cue in track.Cues)
        {
            if (cue.Time < _viewStart - 0.5 || cue.Time > viewEnd + 0.5) continue;
            double x = HeaderWidth + TimelineMath.TimeToX(cue.Time, _viewStart, _pixelsPerSecond);

            if (cue.Mode != VtsCueMode.Toggle)
            {
                dc.DrawGeometry(brush, null, Diamond(x, cy, 4.5));
                var onceLabel = Text(cue.Label, 10, brush);
                if (x + 8 + onceLabel.Width <= HeaderWidth + w)
                    dc.DrawText(onceLabel, new Point(x + 8, row.Top + (row.Height - 13) / 2));
                continue;
            }

            double duration = cue.Duration > 0.01 ? cue.Duration : 2.0;
            double endX = HeaderWidth + TimelineMath.TimeToX(
                Math.Min(cue.Time + duration, viewEnd), _viewStart, _pixelsPerSecond);
            var rect = new Rect(x, row.Top + 3, Math.Max(6, endX - x), row.Height - 6);

            dc.DrawRoundedRectangle(fill, null, rect, 3, 3);
            dc.DrawRectangle(brush, null, new Rect(rect.X, rect.Y, 2, rect.Height));
            dc.DrawEllipse(brush, null, new Point(rect.X + 5, cy), 2.5, 2.5);

            if (rect.Width >= 22)
            {
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawText(Text(cue.Label, 10, brush), new Point(rect.X + 10, row.Top + (row.Height - 13) / 2));
                dc.Pop();
            }
        }
    }

    private void RenderHeader(DrawingContext dc, Row row)
    {
        bool selected = row.Kind == RowKind.Group
                        && string.Equals(row.GroupKey, _selectedOwnerKey, StringComparison.Ordinal);
        var background = row.Kind == RowKind.Group
            ? (selected ? GroupSelectedBrush : GroupHeaderBrush)
            : HeaderBrush;
        dc.DrawRectangle(background, null, new Rect(0, row.Top, HeaderWidth, row.Height));

        if (row.Kind == RowKind.Ruler)
        {
            var head = Text(FormatTime(_snap?.PlayheadSeconds ?? 0), 13, FgBrush, MonoFont);
            dc.DrawText(head, new Point(8, row.Top + 4));
            dc.DrawText(Text("/ " + FormatTime(_snap?.DurationSeconds ?? 0), 10, FaintBrush, MonoFont),
                new Point(12 + head.Width, row.Top + 7));
            DrawHiddenTracksIndicator(dc, row);
        }
        else if (row.Kind == RowKind.Group)
        {
            double cy = row.Top + row.Height / 2;
            if (HasGroupChildren(row.GroupKey)) DrawTwirl(dc, 13, cy, IsGroupExpanded(row.GroupKey));
            else DrawLeafMark(dc, 13, cy);
            var owner = FindOwner(row.GroupKey);
            bool eyeClosed = owner is { Kind: TOwnerKind.Layer }
                ? !owner.Enabled
                : IsGroupHidden(row.GroupKey);
            DrawEye(dc, row, 32, eyeClosed);
            dc.PushClip(new RectangleGeometry(new Rect(44, row.Top, HeaderWidth - 52, row.Height)));
            dc.DrawText(Text(owner?.Label ?? row.GroupKey, 11.5,
                    selected ? FgBrush : DimBrush),
                new Point(46, row.Top + (row.Height - 14) / 2));
            dc.Pop();
            int childCount = GroupChildCount(row.GroupKey);
            if (childCount > 0)
                dc.DrawText(Text(childCount.ToString(CultureInfo.InvariantCulture), 10, FaintBrush, MonoFont),
                    new Point(HeaderWidth - 22, row.Top + (row.Height - 13) / 2));
        }
        else
        {
            DrawEye(dc, row);
            dc.PushClip(new RectangleGeometry(new Rect(26, row.Top, HeaderWidth - 30, row.Height)));
            switch (row.Kind)
            {
                case RowKind.Waveform:
                    dc.DrawText(Text(L.T("音频"), 11, DimBrush),
                        new Point(28, row.Top + (row.Height - 14) / 2));
                    break;
                case RowKind.Lyrics:
                    dc.DrawText(Text(L.T("歌词"), 11, DimBrush),
                        new Point(28, row.Top + (row.Height - 14) / 2));
                    break;
                case RowKind.Automation:
                    var curve = _snap!.Curves[row.Index];
                    var label = string.IsNullOrEmpty(curve.ShortLabel) ? curve.Label : curve.ShortLabel;
                    dc.DrawText(Text(label, 11, DimBrush),
                        new Point(28, row.Top + (row.Height - 14) / 2));
                    break;
                case RowKind.VtsCue:
                    var cueTrack = _snap!.VtsCueTracks[row.Index];
                    dc.DrawText(Text(VtsCueTrackLabel(cueTrack), 11, DimBrush),
                        new Point(28, row.Top + (row.Height - 14) / 2));
                    break;
                case RowKind.Design:
                    if (_snap is not null && row.Index >= 0 && row.Index < _snap.DesignTracks.Count)
                    {
                        var track = _snap.DesignTracks[row.Index];
                        bool empty = track.Segments.Count == 0;
                        dc.DrawText(Text(DesignTrackNames.Label(track.Kind), 11, empty ? FaintBrush : DimBrush),
                            new Point(28, row.Top + (row.Height - 14) / 2));
                    }
                    break;
            }
            dc.Pop();
        }

        dc.DrawLine(SoftPen, new Point(0, row.Top + row.Height), new Point(HeaderWidth, row.Top + row.Height));
    }

    private void RenderGroup(DrawingContext dc, Row row)
    {
        var owner = FindOwner(row.GroupKey);
        if (owner is null) return;

        bool selected = string.Equals(row.GroupKey, _selectedOwnerKey, StringComparison.Ordinal);
        dc.DrawRectangle(selected ? GroupSelectedRowBrush : GroupRowBrush, null,
            new Rect(HeaderWidth, row.Top, ContentWidth, row.Height));
        dc.DrawLine(SoftPen, new Point(HeaderWidth, row.Top + row.Height),
            new Point(HeaderWidth + ContentWidth, row.Top + row.Height));

        double cy = row.Top + row.Height / 2;
        double viewEnd = _viewStart + ContentWidth / _pixelsPerSecond;

        switch (owner.Kind)
        {
            case TOwnerKind.Camera:
            case TOwnerKind.VtsMove:
            case TOwnerKind.Layer:
            case TOwnerKind.Effect:
                for (int i = 0; i < _snap!.Curves.Count; i++)
                {
                    var curve = _snap.Curves[i];
                    if (curve.Address.Kind != owner.AddressKind) continue;
                    if (!string.Equals(curve.Address.Target, owner.Target, StringComparison.Ordinal)) continue;
                    foreach (var key in curve.Keys)
                    {
                        if (key.TimeSeconds < _viewStart || key.TimeSeconds > viewEnd) continue;
                        double x = HeaderWidth + TimelineMath.TimeToX(
                            key.TimeSeconds, _viewStart, _pixelsPerSecond);
                        dc.DrawRectangle(SummaryKeyBrush, null, new Rect(x - 1, cy - 3, 2, 6));
                    }
                }
                break;
            case TOwnerKind.Design:
                for (int i = 0; i < _snap!.DesignTracks.Count; i++)
                {
                    var track = _snap.DesignTracks[i];
                    foreach (var seg in track.Segments)
                    {
                        if (seg.End < _viewStart || seg.Start > viewEnd) continue;
                        double x0 = HeaderWidth + TimelineMath.TimeToX(
                            Math.Max(seg.Start, _viewStart), _viewStart, _pixelsPerSecond);
                        double x1 = HeaderWidth + TimelineMath.TimeToX(
                            Math.Min(seg.End, viewEnd), _viewStart, _pixelsPerSecond);
                        dc.DrawRoundedRectangle(SummarySegmentBrush, null,
                            new Rect(x0, cy - 4, Math.Max(2, x1 - x0), 8), 2, 2);
                    }
                }
                break;
            case TOwnerKind.VtsCue:
                for (int i = 0; i < _snap!.VtsCueTracks.Count; i++)
                {
                    var track = _snap.VtsCueTracks[i];
                    var brush = CueBrush(track.Kind);
                    foreach (var cue in track.Cues)
                    {
                        if (cue.Time < _viewStart || cue.Time > viewEnd) continue;
                        double x = HeaderWidth + TimelineMath.TimeToX(cue.Time, _viewStart, _pixelsPerSecond);
                        dc.DrawEllipse(brush, null, new Point(x, cy), 3, 3);
                    }
                }
                break;
        }
    }

    private static void DrawTwirl(DrawingContext dc, double x, double y, bool expanded)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            if (expanded)
            {
                g.BeginFigure(new Point(x - 4, y - 2), true, true);
                g.LineTo(new Point(x + 4, y - 2), true, false);
                g.LineTo(new Point(x, y + 3), true, false);
            }
            else
            {
                g.BeginFigure(new Point(x - 2, y - 4), true, true);
                g.LineTo(new Point(x + 3, y), true, false);
                g.LineTo(new Point(x - 2, y + 4), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(TwirlBrush, null, geometry);
    }

    private static void DrawEye(DrawingContext dc, in Row row)
    {
        double cx = 14;
        double cy = row.Top + row.Height / 2;
        dc.DrawEllipse(null, EyePen, new Point(cx, cy), 6, 3.5);
        dc.DrawEllipse(EyeBrush, null, new Point(cx, cy), 1.6, 1.6);
    }

    private static void DrawLeafMark(DrawingContext dc, double x, double y)
    {
        dc.DrawEllipse(TwirlBrush, null, new Point(x, y), 2, 2);
    }

    private static void DrawEye(DrawingContext dc, in Row row, double cx, bool closed = false)
    {
        double cy = row.Top + row.Height / 2;
        dc.DrawEllipse(null, closed ? EyeOffPen : EyePen, new Point(cx, cy), 6, 3.5);
        if (closed)
        {
            dc.DrawLine(EyeOffPen, new Point(cx - 6, cy + 3), new Point(cx + 6, cy - 3));
            return;
        }
        dc.DrawEllipse(EyeBrush, null, new Point(cx, cy), 1.6, 1.6);
    }

    private void DrawHiddenTracksIndicator(DrawingContext dc, in Row row)
    {
        int count = HiddenTrackEntries().Count;
        if (count == 0) return;

        double cx = HeaderWidth - 34;
        double cy = row.Top + row.Height / 2;
        dc.DrawEllipse(null, EyeOffPen, new Point(cx, cy), 5.5, 3);
        dc.DrawLine(EyeOffPen, new Point(cx - 7, cy - 4), new Point(cx + 7, cy + 4));
        var text = Text(count.ToString(CultureInfo.InvariantCulture), 10, EyeOffPen.Brush, MonoFont);
        dc.DrawText(text, new Point(HeaderWidth - 22, row.Top + (row.Height - 13) / 2));
    }

    private void RenderPlayhead(DrawingContext dc, double contentH)
    {
        if (_snap is null) return;
        double x = HeaderWidth + TimelineMath.TimeToX(_snap.PlayheadSeconds, _viewStart, _pixelsPerSecond);
        if (x >= HeaderWidth - 6 && x <= HeaderWidth + ContentWidth + 6)
        {
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, contentH));

            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(new Point(x - 5, 0), true, true);
                g.LineTo(new Point(x + 5, 0), true, false);
                g.LineTo(new Point(x, 9), true, false);
            }
            triangle.Freeze();
            dc.DrawGeometry(PlayheadBrush, null, triangle);
        }

        if (_dragTip is { } tip)
        {
            var text = Text(tip, 11, FgBrush, MonoFont);
            var box = new Rect(HeaderWidth + 6, contentH - 26, text.Width + 10, 18);
            dc.DrawRoundedRectangle(TipBrush, null, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 5, box.Y + 2));
        }
    }

    private void RenderScrollbar(DrawingContext dc, double w, double h)
    {
        double top = h - ScrollbarHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, top, w, ScrollbarHeight));
        if (!HorizontalScrollbar(out var trackX, out var trackW, out var thumbX, out var thumbW)) return;

        dc.DrawRectangle(ScrollTrackBrush, null, new Rect(trackX, top + 3, trackW, ScrollbarHeight - 6));
        var thumbBrush = _drag == DragMode.Pan ? ScrollThumbHotBrush : ScrollThumbBrush;
        dc.DrawRoundedRectangle(thumbBrush, null,
            new Rect(thumbX, top + 3, thumbW, ScrollbarHeight - 6), 4, 4);
    }

    private FormattedText Text(string text, double size, Brush brush, Typeface? typeface = null) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface ?? UiFont, size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static StreamGeometry Diamond(double x, double y, double r)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x, y - r), true, true);
            g.LineTo(new Point(x + r, y), true, false);
            g.LineTo(new Point(x, y + r), true, false);
            g.LineTo(new Point(x - r, y), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        return nl < 0 ? text : text[..nl];
    }

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
