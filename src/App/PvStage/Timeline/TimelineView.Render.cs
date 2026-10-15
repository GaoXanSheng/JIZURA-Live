using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace JizuraLive.App.PvStage.Timeline;

public sealed partial class TimelineView
{
    private const double ValuePad = 3;
    private const double KeyRadius = 4;
    private const double HandleRadius = 3.5;

    private static readonly Brush BgBrush = Solid(0x0A, 0x0A, 0x0D);
    private static readonly Brush PanelBrush = Solid(0x10, 0x10, 0x15);
    private static readonly Brush RowBrush = Solid(0x12, 0x12, 0x18);
    private static readonly Brush RowAltBrush = Solid(0x0D, 0x0D, 0x12);
    private static readonly Brush HeaderBrush = Solid(0x14, 0x14, 0x1B);
    private static readonly Brush AccentBrush = Solid(0x5B, 0x8C, 0xFF);
    private static readonly Brush AccentSoftBrush = Solid(0x2A, 0x3A, 0x66);
    private static readonly Brush WaveBrush = Solid(0x38, 0xE1, 0xC4);
    private static readonly Brush FgBrush = Solid(0xEC, 0xEC, 0xF2);
    private static readonly Brush DimBrush = Solid(0x9A, 0x9A, 0xA8);
    private static readonly Brush FaintBrush = Solid(0x5E, 0x5E, 0x6C);
    private static readonly Brush PlayheadBrush = Solid(0xFF, 0x5F, 0x6E);
    private static readonly Brush ConflictBrush = Solid(0xFF, 0x5F, 0x6E);
    private static readonly Brush TipBrush = Solid(0xE6, 0x17, 0x17, 0x1E);
    private static readonly Pen LinePen = FrozenPen(Solid(0x23, 0x23, 0x2C), 1);
    private static readonly Pen SoftPen = FrozenPen(Solid(0x19, 0x19, 0x20), 1);
    private static readonly Pen WavePen = FrozenPen(WaveBrush, 1);
    private static readonly Pen CurvePen = FrozenPen(AccentBrush, 1.4);
    private static readonly Pen KeyPen = FrozenPen(AccentBrush, 1);
    private static readonly Pen SelKeyPen = FrozenPen(FgBrush, 1.4);
    private static readonly Pen HandlePen = FrozenPen(AccentSoftBrush, 1);
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

        dc.PushClip(new RectangleGeometry(contentRect));
        foreach (var row in rows)
        {
            if (row.Top >= contentH) break;
            switch (row.Kind)
            {
                case RowKind.Ruler: RenderRuler(dc, row); break;
                case RowKind.Waveform: RenderWaveform(dc, row); break;
                case RowKind.Lyrics: RenderLyrics(dc, row); break;
                case RowKind.Automation: RenderAutomation(dc, row); break;
            }
        }
        dc.Pop();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, HeaderWidth, h)));
        foreach (var row in rows)
        {
            if (row.Top >= contentH) break;
            RenderHeader(dc, row);
        }
        dc.Pop();

        dc.PushClip(new RectangleGeometry(contentRect));
        RenderPlayhead(dc, contentH);
        dc.Pop();

        dc.DrawLine(LinePen, new Point(HeaderWidth, 0), new Point(HeaderWidth, h));
        RenderScrollbar(dc, w, h);
    }

    private void RenderRuler(DrawingContext dc, Row row)
    {
        double w = ContentWidth;
        dc.DrawRectangle(HeaderBrush, null, new Rect(HeaderWidth, row.Top, w, row.Height));

        double step = TimelineMath.RulerStep(_pixelsPerSecond);
        double end = _viewStart + w / _pixelsPerSecond;
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
        double fallback = (curve.Min + curve.Max) / 2;

        // 曲线画在本轨矩形内：Spring/Bezier 的超调越出 Min/Max 显示域时裁掉，而不是钳回显示域
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, row.Top, w, row.Height)));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool started = false;
            for (double x = 0; x <= w; x += 1)
            {
                double v = TimelineMath.Evaluate(curve.Keys, TimeAt(x), fallback);
                var point = new Point(HeaderWidth + x, TimelineMath.ValueToY(v, curve.Min, curve.Max, top, height));
                if (!started)
                {
                    g.BeginFigure(point, false, false);
                    started = true;
                }
                else g.LineTo(point, true, false);
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

        foreach (var (index, outSide) in HandleTargets(curve))
        {
            var key = curve.Keys[index];
            var anchor = new Point(
                HeaderWidth + TimelineMath.TimeToX(key.TimeSeconds, _viewStart, _pixelsPerSecond),
                TimelineMath.ValueToY(key.Value, curve.Min, curve.Max, top, height));
            var handle = HandlePixel(curve, row, index, outSide);
            dc.DrawLine(HandlePen, anchor, handle);
            dc.DrawEllipse(AccentBrush, null, handle, HandleRadius, HandleRadius);
        }

        dc.Pop();
    }

    private void RenderHeader(DrawingContext dc, Row row)
    {
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, row.Top, HeaderWidth, row.Height));
        switch (row.Kind)
        {
            case RowKind.Ruler:
                var head = Text(FormatTime(_snap?.PlayheadSeconds ?? 0), 13, FgBrush, MonoFont);
                dc.DrawText(head, new Point(8, row.Top + 4));
                dc.DrawText(Text("/ " + FormatTime(_snap?.DurationSeconds ?? 0), 10, FaintBrush, MonoFont),
                    new Point(12 + head.Width, row.Top + 7));
                break;
            case RowKind.Automation:
                dc.DrawText(Text(_snap!.Curves[row.Index].Label, 11, DimBrush), new Point(8, row.Top + 5));
                break;
        }
        dc.DrawLine(SoftPen, new Point(0, row.Top + row.Height), new Point(HeaderWidth, row.Top + row.Height));
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
        if (_snap is null) return;

        double trackX = HeaderWidth;
        double trackW = Math.Max(1, w - HeaderWidth);
        dc.DrawRectangle(RowBrush, null, new Rect(trackX, top + 2, trackW, ScrollbarHeight - 4));

        double duration = Math.Max(_snap.DurationSeconds, TimelineMath.MinSecondsPerScreen);
        double visible = Math.Min(duration, trackW / _pixelsPerSecond);
        double thumbW = Math.Min(trackW, Math.Max(24, trackW * visible / duration));
        double thumbX = trackX + Math.Min(trackW - thumbW, trackW * _viewStart / duration);
        dc.DrawRoundedRectangle(AccentSoftBrush, null, new Rect(thumbX, top + 2, thumbW, ScrollbarHeight - 4), 3, 3);
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
