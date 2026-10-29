using System.Numerics;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

public readonly record struct TransitionWindow(double Start, double End)
{
    public bool IsValid => double.IsFinite(Start) && double.IsFinite(End) && End > Start;

    public double Progress(double time) =>
        IsValid ? Math.Clamp((time - Start) / (End - Start), 0.0, 1.0) : time >= Start ? 1.0 : 0.0;
}

public sealed class TransitionParams
{
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Colors { get; } = new(StringComparer.Ordinal);

    public int Seed { get; set; }

    public double Num(string key, double fallback = 0) =>
        Values.TryGetValue(key, out var v) && double.IsFinite(v) ? v : fallback;

    public Color4 Col(string key, string fallback) =>
        PvScene.ParseHex(Colors.TryGetValue(key, out var v) ? v : fallback);
}

public readonly record struct TransitionFrame(
    ID2D1Bitmap1 A,
    ID2D1Bitmap1 B,
    int Width,
    int Height);

public delegate void TransitionBlendFn(Immediate2D draw, TransitionFrame frame, double p,
    TransitionParams pars);

public sealed class TransitionTechnique
{
    public required string Id { get; init; }

    public TransitionParams Params { get; } = new();

    public required TransitionBlendFn Blend { get; init; }
}

public sealed class TransitionRunner : IDisposable
{
    private readonly Immediate2D _draw;
    private readonly GpuTexturePool _pool;
    private readonly int _width;
    private readonly int _height;

    private GpuTexturePool.Entry? _entryA;
    private GpuTexturePool.Entry? _entryB;
    private ID2D1Bitmap1? _bitmapA;
    private ID2D1Bitmap1? _bitmapB;
    private bool _disposed;

    public TransitionRunner(Immediate2D draw, GpuTexturePool pool, int width, int height)
    {
        _draw = draw;
        _pool = pool;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public ID2D1Bitmap1? SurfaceA => _bitmapA;

    public ID2D1Bitmap1? SurfaceB => _bitmapB;

    public void Acquire()
    {
        if (_bitmapA is not null && _bitmapB is not null) return;

        Release();

        var a = _pool.Rent(_width, _height);
        var b = _pool.Rent(_width, _height);
        try
        {
            _bitmapA = CreateBitmap(a);
            _bitmapB = CreateBitmap(b);
        }
        catch
        {
            _bitmapA?.Dispose();
            _bitmapB?.Dispose();
            _pool.Return(a);
            _pool.Return(b);
            _bitmapA = null;
            _bitmapB = null;
            throw;
        }

        _entryA = a;
        _entryB = b;
    }

    public void Release()
    {
        _bitmapA?.Dispose();
        _bitmapB?.Dispose();
        if (_entryA is { } a) _pool.Return(a);
        if (_entryB is { } b) _pool.Return(b);
        _entryA = null;
        _entryB = null;
        _bitmapA = null;
        _bitmapB = null;
    }

    public void Composite(ID2D1Bitmap1 target, Rect dest, TransitionTechnique? technique, double p,
        TransitionParams? pars = null)
    {
        if (_bitmapA is not { } a || _bitmapB is not { } b) return;

        _draw.SetTarget(target, _width, _height);
        _draw.Begin();
        try
        {
            if (!(p > 0))
            {
                _draw.DrawBitmap(a, dest);
                return;
            }

            if (p >= 1)
            {
                _draw.DrawBitmap(b, dest);
                return;
            }

            if (technique is null) return;

            var effective = pars ?? technique.Params;
            ApplyPlan(technique.Id, effective);

            _draw.Save();
            _draw.Translate(dest.Left, dest.Top);
            _draw.Scale(dest.Width / _width, dest.Height / _height);
            _draw.PushSurface(_width, _height, clear: true);
            try
            {
                technique.Blend(_draw, new TransitionFrame(a, b, _width, _height), p, effective);
            }
            catch (Exception ex)
            {
                Log.Warn($"[PV 转场] {technique.Id}：{ex.Message}");
            }

            _draw.PopSurface(new Paint2D());
            _draw.Restore();
        }
        finally
        {
            _draw.End();
        }
    }

    private static void ApplyPlan(string id, TransitionParams pars)
    {
        var rng = new CameraRng(unchecked((uint)pars.Seed));
        switch (id)
        {
            case "wipe":
                Plan(pars, "dir", DirPick(rng, 3.0, 2.0, 1.4, 0.8));
                break;
            case "diagonalWipe":
                Plan(pars, "k", rng.Range(0.3, 0.55) * (Chance(rng, 0.5) != 0 ? 1 : -1));
                Plan(pars, "rev", Chance(rng, 0.4));
                break;
            case "clockWipe":
                Plan(pars, "dir", Chance(rng, 0.7) != 0 ? 1 : -1);
                Plan(pars, "a0", rng.Pick(new[] { -90.0, -90.0, 0.0, 180.0 }));
                break;
            case "irisOpen":
                Plan(pars, "x", 0.5 + rng.Range(-0.12, 0.12));
                Plan(pars, "y", 0.5 + rng.Range(-0.1, 0.1));
                break;
            case "pushSlide":
                Plan(pars, "dir", DirPick(rng, 3.0, 1.6, 1.4, 0.6));
                break;
            case "cover":
                Plan(pars, "dir", DirPick(rng, 2.0, 2.0, 1.5, 1.0));
                break;
            case "uncover":
                Plan(pars, "dir", DirPick(rng, 2.0, 2.0, 1.6, 1.0));
                break;
            case "zoomThrough":
                Plan(pars, "z", rng.Range(1.5, 2.2));
                break;
            case "doorsOpen":
                Plan(pars, "vert", Chance(rng, 0.3));
                break;
            case "blinds":
                Plan(pars, "n", Int(rng, 7, 12));
                Plan(pars, "vert", Chance(rng, 0.35));
                Plan(pars, "rev", Chance(rng, 0.4));
                break;
            case "checker":
                Plan(pars, "n", Int(rng, 4, 6));
                Plan(pars, "rev", Chance(rng, 0.5));
                break;
            case "blockDissolve":
                Plan(pars, "n", Int(rng, 7, 11));
                Plan(pars, "side", rng.Pick(new[] { 0.0, 0.0, 1.0, 2.0 }));
                break;
            case "whipPan":
                Plan(pars, "dir", Chance(rng, 0.65) != 0 ? -1 : 1);
                Plan(pars, "vert", Chance(rng, 0.2));
                break;
            case "spinOut":
                Plan(pars, "rot", rng.Range(100, 200) * (Chance(rng, 0.5) != 0 ? 1 : -1));
                break;
            case "inkBlob":
                Plan(pars, "x", rng.Pick(new[] { 0.5, 0.5, 0.15, 0.85 }) + rng.Range(-0.08, 0.08));
                Plan(pars, "y", rng.Pick(new[] { 0.5, 0.25, 0.8 }) + rng.Range(-0.06, 0.06));
                break;
            case "shatterTiles":
                Plan(pars, "n", Int(rng, 6, 9));
                Plan(pars, "x", rng.Range(0.3, 0.7));
                Plan(pars, "y", rng.Range(0.3, 0.6));
                break;
            case "sliceShift":
                Plan(pars, "n", Int(rng, 5, 9));
                Plan(pars, "vert", Chance(rng, 0.25));
                break;
            case "cubeTurn":
                Plan(pars, "dir", Chance(rng, 0.6) != 0 ? 1 : -1);
                break;
            case "flashCross":
                Plan(pars, "useAccent", Chance(rng, 0.3));
                break;
            case "pixelate":
                Plan(pars, "k", rng.Range(11, 17));
                break;
            case "hrStaticCut":
                Plan(pars, "roll", Chance(rng, 0.6));
                break;
            case "hrBlink":
                Plan(pars, "half", Chance(rng, 0.3));
                break;
            case "tyRuleWipe":
                Plan(pars, "n", Int(rng, 7, 12));
                Plan(pars, "rtl", Chance(rng, 0.25));
                break;
            case "tyGridCells":
                Plan(pars, "rows", Int(rng, 4, 6));
                Plan(pars, "tate", Chance(rng, 0.7));
                break;
            case "knCornerSwing":
                Plan(pars, "c", Int(rng, 0, 3));
                break;
            case "knStutterCut":
                Plan(pars, "z", rng.Range(1.05, 1.1));
                Plan(pars, "o", rng.Range(0.02, 0.035) * rng.Pick(new[] { 1.0, -1.0 }));
                break;
            case "knStripSlam":
                Plan(pars, "n", Int(rng, 3, 5));
                Plan(pars, "rev", Chance(rng, 0.5));
                Plan(pars, "up", Chance(rng, 0.25));
                break;
            case "channelSwitch":
                Plan(pars, "seed", Int(rng, 1, 1000000000));
                break;
        }
    }

    private static void Plan(TransitionParams pars, string key, double value)
    {
        if (!pars.Values.ContainsKey(key)) pars.Values[key] = value;
    }

    private static double Chance(CameraRng rng, double p) => rng.Next() < p ? 1.0 : 0.0;

    private static int Int(CameraRng rng, int lo, int hi) =>
        lo + Math.Min(hi - lo, (int)Math.Floor(rng.Next() * (hi - lo + 1)));

    private static double DirPick(CameraRng rng, double wl, double wr, double wu, double wd) =>
        rng.WPick(new (double V, double W)[] { (0, wl), (1, wr), (2, wu), (3, wd) });

    public static Dictionary<string, double> Defaults(string? id, int seed)
    {
        var pars = new TransitionParams { Seed = seed };
        if (id is not null) ApplyPlan(id, pars);
        return new Dictionary<string, double>(pars.Values, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private ID2D1Bitmap1 CreateBitmap(GpuTexturePool.Entry entry)
    {
        using var surface = entry.Texture.QueryInterface<IDXGISurface>();
        return _draw.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target));
    }
}

public static class TransitionCatalog
{
    private const double Deg = Math.PI / 180.0;
    private const double Tau = Math.PI * 2.0;

    private static readonly Dictionary<string, TransitionTechnique> ById = new(StringComparer.Ordinal);

    static TransitionCatalog()
    {
        RegisterWipe();
        RegisterDiagonalWipe();
        RegisterClockWipe();
        RegisterIrisOpen();
        RegisterPushSlide();
        RegisterCover();
        RegisterUncover();
        RegisterZoomThrough();
        RegisterDoorsOpen();
        RegisterBlinds();
        RegisterChecker();
        RegisterBlockDissolve();
        RegisterPixelate();
        RegisterFlashCross();
        RegisterWhipPan();
        RegisterSpinOut();
        RegisterInkBlob();
        RegisterShatterTiles();
        RegisterSliceShift();
        RegisterCubeTurn();
        RegisterHrStaticCut();
        RegisterHrBlink();
        RegisterTyRuleWipe();
        RegisterTyGridCells();
        RegisterKnCornerSwing();
        RegisterKnStutterCut();
        RegisterKnStripSlam();
        RegisterChannelSwitch();
    }

    public static void Register(TransitionTechnique technique) => ById[technique.Id] = technique;

    public static IReadOnlyList<string> Ids => [.. ById.Keys];

    public static TransitionTechnique? Get(string? id) =>
        id is not null && ById.TryGetValue(id, out var technique) ? technique : null;

    private static void RegisterWipe() => Register(new TransitionTechnique
    {
        Id = "wipe",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var lw = LwOf(f, 0.007);
            var ac = Accent(pars);
            DrawImage(d, f.A, cw, ch, 0, 0);

            double bx, by, bw, bh, off;
            var dir = (int)pars.Num("dir", 1);
            if (dir == 1)
            {
                var x = cw * e;
                PartImage(d, f.B, cw, ch, 0, 0, x, ch);
                (bx, by, bw, bh, off) = (x - lw / 2, 0, lw, ch, -1);
            }
            else if (dir == 2)
            {
                var y = ch * (1 - e);
                PartImage(d, f.B, cw, ch, 0, y, cw, ch - y);
                (bx, by, bw, bh, off) = (0, y - lw / 2, cw, lw, 1);
            }
            else if (dir == 3)
            {
                var y = ch * e;
                PartImage(d, f.B, cw, ch, 0, 0, cw, y);
                (bx, by, bw, bh, off) = (0, y - lw / 2, cw, lw, -1);
            }
            else
            {
                var x = cw * (1 - e);
                PartImage(d, f.B, cw, ch, x, 0, cw - x, ch);
                (bx, by, bw, bh, off) = (x - lw / 2, 0, lw, ch, 1);
            }

            var alpha = Math.Pow(Bell(p), 0.6);
            Fill(d, bx, by, bw, bh, ac, alpha);
            var dx = lw * 3.2 * off;
            var side = bw == lw;
            Fill(d, side ? bx + dx : 0, side ? 0 : by + dx,
                side ? Math.Max(1, lw * 0.35) : cw, side ? ch : Math.Max(1, lw * 0.35), ac, alpha * 0.5);
        },
    });

    private static void RegisterDiagonalWipe() => Register(new TransitionTechnique
    {
        Id = "diagonalWipe",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var rev = pars.Num("rev", 0) != 0;
            var sl = pars.Num("k", 0.42) * ch;
            var msl = rev ? -sl : sl;
            var band = MinD(f) * 0.07 * Bell(p);
            var span = cw + Math.Abs(sl) + band * 2 + 4;
            var x = -Math.Abs(sl) / 2 - band - 2 + span * e;

            DrawImage(d, f.A, cw, ch, 0, 0);
            using (var clip = Quad(d, Mirror(rev, cw, -cw * 2.0), Mirror(rev, cw, x), msl, ch))
            {
                d.Save();
                d.ClipPath(clip, antialias: false);
                DrawImage(d, f.B, cw, ch, 0, 0);
                d.Restore();
            }

            if (band > 0.5)
            {
                FillQuad(d, Mirror(rev, cw, x), Mirror(rev, cw, x + band), msl, ch, Accent(pars), 1);
                FillQuad(d, Mirror(rev, cw, x + band * 1.35), Mirror(rev, cw, x + band * 1.6), msl, ch,
                    Accent2(pars), 0.85);
            }
        },
    });

    private static void RegisterClockWipe() => Register(new TransitionTechnique
    {
        Id = "clockWipe",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var cx = cw / 2.0;
            var cy = ch / 2.0;
            var r = Math.Sqrt(cw * cw + ch * ch) / 2 + 4;
            var a0 = pars.Num("a0", -90) * Deg;
            var dir = pars.Num("dir", 1);
            var a1 = a0 + e * Tau * dir;

            DrawImage(d, f.A, cw, ch, 0, 0);
            using (var clip = Pie(d, cx, cy, r, a0, a1))
            {
                d.Save();
                d.ClipPath(clip, antialias: false);
                DrawImage(d, f.B, cw, ch, 0, 0);
                d.Restore();
            }

            var alpha = Math.Pow(Bell(p), 0.5);
            var lw = LwOf(f, 0.006);
            StrokeLine(d, cx, cy, cx + Math.Cos(a1) * r, cy + Math.Sin(a1) * r, Accent(pars), lw, alpha);
            Fill(d, cx - lw * 2.2, cy - lw * 2.2, lw * 4.4, lw * 4.4, Accent(pars), alpha);
        },
    });

    private static void RegisterIrisOpen() => Register(new TransitionTechnique
    {
        Id = "irisOpen",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var cx = cw * pars.Num("x", 0.5);
            var cy = ch * pars.Num("y", 0.5);
            var e = InOutCubic(p);
            var r = Math.Sqrt(Math.Pow(Math.Max(cx, cw - cx), 2) + Math.Pow(Math.Max(cy, ch - cy), 2)) + 4;
            var rr = r * e;

            DrawImage(d, f.A, cw, ch, 0, 0);
            if (rr > 0.5)
            {
                using var clip = new Path2D(d.Factory).AddCircle((float)cx, (float)cy, (float)rr);
                d.Save();
                d.ClipPath(clip, antialias: true);
                DrawImage(d, f.B, cw, ch, 0, 0);
                d.Restore();
            }

            var alpha = Math.Pow(Bell(p), 0.6);
            var lw = LwOf(f, 0.009);
            StrokeCircle(d, cx, cy, rr + lw / 2, Accent(pars), lw, alpha);
            StrokeCircle(d, cx, cy, rr * 1.06 + lw * 2.5, Accent2(pars), lw * 0.4, alpha * 0.6);
        },
    });

    private static void RegisterPushSlide() => Register(new TransitionTechnique
    {
        Id = "pushSlide",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var dir = (int)pars.Num("dir", 1);
            var h = dir <= 1;
            var l = h ? cw : ch;
            var sg = dir is 0 or 2 ? -1 : 1;
            var off = Math.Round(e * l, MidpointRounding.AwayFromZero) * sg;

            if (h)
            {
                DrawImage(d, f.A, cw, ch, off, 0);
                DrawImage(d, f.B, cw, ch, off - sg * cw, 0);
            }
            else
            {
                DrawImage(d, f.A, cw, ch, 0, off);
                DrawImage(d, f.B, cw, ch, 0, off - sg * ch);
            }

            var lw = LwOf(f, 0.005);
            var q = h ? (sg < 0 ? cw + off : off) : (sg < 0 ? ch + off : off);
            var alpha = Math.Pow(Bell(p), 0.6);
            if (h) Fill(d, q - lw / 2, 0, lw, ch, Accent(pars), alpha);
            else Fill(d, 0, q - lw / 2, cw, lw, Accent(pars), alpha);
        },
    });

    private static void RegisterCover() => Register(new TransitionTechnique
    {
        Id = "cover",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var dir = (int)pars.Num("dir", 1);
            var h = dir <= 1;
            var sg = dir is 0 or 2 ? -1 : 1;
            var l = h ? cw : ch;
            var bo = Math.Round((1 - e) * l, MidpointRounding.AwayFromZero) * -sg;
            var ao = Math.Round(e * l * 0.18, MidpointRounding.AwayFromZero) * sg;

            if (h) DrawImage(d, f.A, cw, ch, ao, 0);
            else DrawImage(d, f.A, cw, ch, 0, ao);
            Fill(d, 0, 0, cw, ch, Black, 0.45 * e);

            var sw = MinD(f) * 0.06;
            var edge = h ? (sg > 0 ? cw + bo : bo) : (sg > 0 ? ch + bo : bo);
            ShadowBand(d, h, cw, ch, edge, sg * sw, 0.45 * Bell(p));

            if (h) DrawImage(d, f.B, cw, ch, bo, 0);
            else DrawImage(d, f.B, cw, ch, 0, bo);
        },
    });

    private static void RegisterUncover() => Register(new TransitionTechnique
    {
        Id = "uncover",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var dir = (int)pars.Num("dir", 1);
            var h = dir <= 1;
            var sg = dir is 0 or 2 ? -1 : 1;
            var s = 1.05 - 0.05 * OutCubic(p);

            Fill(d, 0, 0, cw, ch, Bg(pars));
            ScaledImage(d, f.B, cw, ch, s);
            Fill(d, 0, 0, cw, ch, Black, 0.4 * (1 - e));

            var l = h ? cw : ch;
            var ao = Math.Round(e * l, MidpointRounding.AwayFromZero) * sg;
            var edge = h ? (sg > 0 ? ao : cw + ao) : (sg > 0 ? ao : ch + ao);
            var sw = MinD(f) * 0.07;
            ShadowBand(d, h, cw, ch, edge, -sg * sw, 0.5 * Bell(p));

            if (h) DrawImage(d, f.A, cw, ch, ao, 0);
            else DrawImage(d, f.A, cw, ch, 0, ao);
        },
    });

    private static void RegisterZoomThrough() => Register(new TransitionTechnique
    {
        Id = "zoomThrough",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var z = pars.Num("z", 1.8);
            var sa = 1 + (z - 1) * Math.Pow(p, 1.4);
            var aa = 1 - InCubic(Math.Clamp(p * 1.12, 0, 1));
            var sb = 0.86 + 0.14 * OutCubic(p);

            Fill(d, 0, 0, cw, ch, Bg(pars));
            ScaledImage(d, f.B, cw, ch, sb);
            if (aa > 0.003)
            {
                ScaledImage(d, f.A, cw, ch, sa, aa, smooth: false);
                if (pars.Num("filter", 1) != 0 && p > 0.08 && aa > 0.15)
                    ScaledImage(d, f.A, cw, ch, sa * (1 + 0.08 * p), aa * 0.35, smooth: false);
            }
        },
    });

    private static void RegisterDoorsOpen() => Register(new TransitionTechnique
    {
        Id = "doorsOpen",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InOutCubic(p);
            var lw = LwOf(f, 0.005);
            var ac = Accent(pars);
            var sb = 0.93 + 0.07 * OutCubic(p);

            Fill(d, 0, 0, cw, ch, Bg(pars));
            ScaledImage(d, f.B, cw, ch, sb);
            Fill(d, 0, 0, cw, ch, Black, 0.35 * (1 - e));

            var alpha = Math.Pow(Bell(p), 0.5);
            if (pars.Num("vert", 0) == 0)
            {
                var hw = (int)Math.Floor(cw / 2.0);
                var off = Math.Round(e * (cw - hw + lw * 2), MidpointRounding.AwayFromZero);
                PartImage(d, f.A, cw, ch, 0, 0, hw, ch, -off, 0);
                PartImage(d, f.A, cw, ch, hw, 0, cw - hw, ch, off, 0);
                Fill(d, hw - off - lw, 0, lw, ch, ac, alpha);
                Fill(d, hw + off, 0, lw, ch, ac, alpha);
            }
            else
            {
                var hh = (int)Math.Floor(ch / 2.0);
                var off = Math.Round(e * (ch - hh + lw * 2), MidpointRounding.AwayFromZero);
                PartImage(d, f.A, cw, ch, 0, 0, cw, hh, 0, -off);
                PartImage(d, f.A, cw, ch, 0, hh, cw, ch - hh, 0, off);
                Fill(d, 0, hh - off - lw, cw, lw, ac, alpha);
                Fill(d, 0, hh + off, cw, lw, ac, alpha);
            }
        },
    });

    private static void RegisterBlinds() => Register(new TransitionTechnique
    {
        Id = "blinds",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 9));
            var vert = pars.Num("vert", 0) != 0;
            var rev = pars.Num("rev", 0) != 0;
            var l = vert ? cw : ch;
            var lw = Math.Max(1, LwOf(f, 0.003));
            var ac = Accent(pars);

            DrawImage(d, f.A, cw, ch, 0, 0);
            for (var i = 0; i < n; i++)
            {
                var k = rev ? n - 1 - i : i;
                var q = InOutCubic(Math.Clamp(p * 1.55 - k / (double)n * 0.55, 0, 1));
                var a0 = Math.Round(i * l / (double)n, MidpointRounding.AwayFromZero);
                var a1 = Math.Round((i + 1) * l / (double)n, MidpointRounding.AwayFromZero);
                if (q <= 0) continue;

                var len = q >= 1 ? a1 - a0 : (a1 - a0) * q;
                if (vert) PartImage(d, f.B, cw, ch, a0, 0, len, ch);
                else PartImage(d, f.B, cw, ch, 0, a0, cw, len);

                if (q < 1)
                {
                    if (vert) Fill(d, a0 + len, 0, lw, ch, ac, 1 - q);
                    else Fill(d, 0, a0 + len, cw, lw, ac, 1 - q);
                }
            }
        },
    });

    private static void RegisterChecker() => Register(new TransitionTechnique
    {
        Id = "checker",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 5));
            var cell = MinD(f) / n;
            var cols = (int)Math.Ceiling(cw / cell);
            var rows = (int)Math.Ceiling(ch / cell);
            var rev = pars.Num("rev", 0) != 0;

            DrawImage(d, f.A, cw, ch, 0, 0);
            for (var j = 0; j < rows; j++)
                for (var i = 0; i < cols; i++)
                {
                    var delay = ((i + j) % 2) * 0.32
                                + 0.18 * ((rev ? cols - 1 - i : i) + j) / Math.Max(1, cols + rows - 2);
                    var t = Math.Clamp((p - delay) / 0.5, 0, 1);
                    if (t <= 0) continue;

                    var x0 = Math.Round(i * cw / (double)cols, MidpointRounding.AwayFromZero);
                    var x1 = Math.Round((i + 1) * cw / (double)cols, MidpointRounding.AwayFromZero);
                    var y0 = Math.Round(j * ch / (double)rows, MidpointRounding.AwayFromZero);
                    var y1 = Math.Round((j + 1) * ch / (double)rows, MidpointRounding.AwayFromZero);

                    if (t >= 1)
                    {
                        PartImage(d, f.B, cw, ch, x0, y0, x1 - x0, y1 - y0);
                        continue;
                    }

                    var q = OutCubic(t);
                    if (q > 0.97)
                    {
                        PartImage(d, f.B, cw, ch, x0, y0, x1 - x0, y1 - y0);
                        continue;
                    }

                    var w = (x1 - x0) * q;
                    var h = (y1 - y0) * q;
                    PartImage(d, f.B, cw, ch, (x0 + x1) / 2 - w / 2, (y0 + y1) / 2 - h / 2, w, h);
                }
        },
    });

    private static void RegisterBlockDissolve() => Register(new TransitionTechnique
    {
        Id = "blockDissolve",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 9));
            var side = (int)pars.Num("side", 0);
            var cell = MinD(f) / n;
            var cols = (int)Math.Ceiling(cw / cell);
            var rows = (int)Math.Ceiling(ch / cell);
            var seed = pars.Seed;
            var ac = Accent(pars);

            DrawImage(d, f.A, cw, ch, 0, 0);
            for (var j = 0; j < rows; j++)
                for (var i = 0; i < cols; i++)
                {
                    var bias = side == 1 ? i / (double)Math.Max(1, cols - 1)
                        : side == 2 ? j / (double)Math.Max(1, rows - 1) : 0.5;
                    var r = 0.02 + 0.84 * (side != 0
                        ? 0.55 * R(seed, i, j, 5) + 0.45 * bias
                        : R(seed, i, j, 5));
                    if (p < r) continue;

                    var x0 = Math.Round(i * cw / (double)cols, MidpointRounding.AwayFromZero);
                    var x1 = Math.Round((i + 1) * cw / (double)cols, MidpointRounding.AwayFromZero);
                    var y0 = Math.Round(j * ch / (double)rows, MidpointRounding.AwayFromZero);
                    var y1 = Math.Round((j + 1) * ch / (double)rows, MidpointRounding.AwayFromZero);

                    PartImage(d, f.B, cw, ch, x0, y0, x1 - x0, y1 - y0);
                    var fade = 1 - (p - r) / 0.1;
                    if (fade > 0) Fill(d, x0, y0, x1 - x0, y1 - y0, ac, fade * 0.75);
                }
        },
    });

    private static void RegisterPixelate() => Register(new TransitionTechnique
    {
        Id = "pixelate",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var maxB = MinD(f) / Math.Max(1, pars.Num("k", 14));
            var ta = InCubic(Math.Clamp(p / 0.55, 0, 1));
            var tb = InCubic(Math.Clamp((1 - p) / 0.55, 0, 1));
            var x = Smooth(0.4, 0.6, p);

            PixelPass(d, f.A, cw, ch, maxB, ta, 1);
            PixelPass(d, f.B, cw, ch, maxB, tb, x);
        },
    });

    private static void RegisterFlashCross() => Register(new TransitionTechnique
    {
        Id = "flashCross",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var x = Smooth(0.3, 0.62, p);

            DrawImage(d, f.A, cw, ch, 0, 0);
            if (x > 0) DrawImage(d, f.B, cw, ch, 0, 0, x);

            var alpha = p < 0.45 ? InQuad(p / 0.45) : 1 - OutCubic((p - 0.45) / 0.55);
            var light = Lum(Bg(pars)) > 0.62;
            var flash = pars.Num("useAccent", 0) != 0 || light ? Accent(pars) : White;
            if (alpha > 0.003) Fill(d, 0, 0, cw, ch, flash, alpha * 0.92);
        },
    });

    private static void RegisterWhipPan() => Register(new TransitionTechnique
    {
        Id = "whipPan",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = IoQuart(p);
            var vert = pars.Num("vert", 0) != 0;
            var dir = pars.Num("dir", 1);
            var l = vert ? ch : cw;
            var off = e * l * dir;
            var blur = l * 0.13 * Math.Pow(Bell(p), 2);
            var bg = Bg(pars);

            if (blur < 3)
            {
                Fill(d, 0, 0, cw, ch, bg, 1);
                if (vert)
                {
                    DrawImage(d, f.A, cw, ch, 0, off);
                    DrawImage(d, f.B, cw, ch, 0, off - l * dir);
                }
                else
                {
                    DrawImage(d, f.A, cw, ch, off, 0);
                    DrawImage(d, f.B, cw, ch, off - l * dir, 0);
                }

                return;
            }

            const double k = 1 / 3.0;
            var w = Math.Max(2, (int)Math.Round(cw * k));
            var h = Math.Max(2, (int)Math.Round(ch * k));
            d.Save();
            d.Scale((float)(cw / (double)w), (float)(ch / (double)h));
            d.PushSurface(w, h, clear: true);
            Fill(d, 0, 0, w, h, bg, 1);
            const int n = 12;
            for (var q = 0; q < n; q++)
            {
                var o = (q / (double)(n - 1) - 0.5) * blur;
                var alpha = 1.0 / (q + 1);
                Echo(d, f.A, cw, ch, w, h, off + o, vert, alpha);
                Echo(d, f.B, cw, ch, w, h, off - l * dir + o, vert, alpha);
            }

            d.PopSurface(new Paint2D());
            d.Restore();
        },
    });

    private static void RegisterSpinOut() => Register(new TransitionTechnique
    {
        Id = "spinOut",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var e = InCubic(p);
            var s = 1 - e;
            var sb = 1.08 - 0.08 * OutCubic(p);

            Fill(d, 0, 0, cw, ch, Bg(pars), 1);
            ScaledImage(d, f.B, cw, ch, sb);
            Fill(d, 0, 0, cw, ch, Black, 0.4 * (1 - OutCubic(p)));
            if (s < 0.004) return;

            d.Save();
            d.Translate((float)(cw / 2.0), (float)(ch / 2.0));
            d.Rotate((float)(pars.Num("rot", 140) * e));
            d.Scale((float)s, (float)s);
            Fill(d, -cw / 2.0 + MinD(f) * 0.02, -ch / 2.0 + MinD(f) * 0.03, cw, ch, Black, 0.35);
            DrawImage(d, f.A, cw, ch, -cw / 2.0, -ch / 2.0);
            var lw = LwOf(f, 0.008) / s;
            StrokeRect(d, -cw / 2.0 + lw / 2, -ch / 2.0 + lw / 2, cw - lw, ch - lw,
                Accent(pars), lw, Math.Min(1, p * 6));
            d.Restore();
        },
    });

    private static void RegisterInkBlob() => Register(new TransitionTechnique
    {
        Id = "inkBlob",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var cx = cw * pars.Num("x", 0.5);
            var cy = ch * pars.Num("y", 0.5);
            var seed = pars.Seed;
            var r = Math.Sqrt(Math.Pow(Math.Max(cx, cw - cx), 2) + Math.Pow(Math.Max(cy, ch - cy), 2)) * 1.36;
            var e = InOutSine(p);
            var rr = r * e;
            var ph = p * 2.4;

            DrawImage(d, f.A, cw, ch, 0, 0);
            var rimA = 1 - Smooth(0.75, 0.98, p);
            var rim = MinD(f) * 0.035 * (0.4 + e);
            if (rimA > 0.01)
            {
                using (var path = BlobPath(d, cx, cy, rr + rim, seed, ph))
                    d.FillPath(path, new Paint2D
                    {
                        Brush = new SolidBrush2D(Accent(pars)),
                        Alpha = (float)Math.Min(1, rimA),
                    });

                for (var k = 0; k < 6; k++)
                {
                    var ang = R(seed, k, 7) * Tau;
                    var dist = rr * (1.12 + 0.3 * R(seed, k, 8)) + rim;
                    var r2 = MinD(f) * (0.008 + 0.02 * R(seed, k, 9)) * Math.Clamp(p * 4, 0, 1);
                    d.FillCircle((float)(cx + Math.Cos(ang) * dist), (float)(cy + Math.Sin(ang) * dist),
                        (float)r2, new Paint2D
                        {
                            Brush = new SolidBrush2D(Accent(pars)),
                            Alpha = (float)Math.Min(1, rimA),
                        });
                }
            }

            if (rr > 0.5)
            {
                using var clip = BlobPath(d, cx, cy, rr, seed, ph);
                d.Save();
                d.ClipPath(clip, antialias: true);
                DrawImage(d, f.B, cw, ch, 0, 0);
                d.Restore();
            }
        },
    });

    private static void RegisterShatterTiles() => Register(new TransitionTechnique
    {
        Id = "shatterTiles",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 7));
            var cell = Math.Max(cw, ch) / (double)n;
            var cols = (int)Math.Ceiling(cw / cell);
            var rows = (int)Math.Ceiling(ch / cell);
            var seed = pars.Seed;
            var cx = cw * pars.Num("x", 0.5);
            var cy = ch * pars.Num("y", 0.45);
            var dAll = Math.Sqrt(cw * cw + ch * ch);

            DrawImage(d, f.B, cw, ch, 0, 0);
            Fill(d, 0, 0, cw, ch, Black, 0.3 * (1 - OutCubic(p)));

            using var still = new Path2D(d.Factory);
            var any = false;
            var moving = new List<(double X0, double Y0, double X1, double Y1, double T, int I, int J)>();
            for (var j = 0; j < rows; j++)
                for (var i = 0; i < cols; i++)
                {
                    var x0 = Math.Round(i * cw / (double)cols, MidpointRounding.AwayFromZero);
                    var x1 = Math.Round((i + 1) * cw / (double)cols, MidpointRounding.AwayFromZero);
                    var y0 = Math.Round(j * ch / (double)rows, MidpointRounding.AwayFromZero);
                    var y1 = Math.Round((j + 1) * ch / (double)rows, MidpointRounding.AwayFromZero);
                    var dist = 0.5 * Math.Sqrt(Math.Pow((x0 + x1) / 2 - cx, 2) + Math.Pow((y0 + y1) / 2 - cy, 2))
                               / dAll * 1.4 + 0.08 * R(seed, i, j, 3);
                    var t = Math.Clamp((p - Math.Min(0.55, dist)) / 0.45, 0, 1);
                    if (t <= 0)
                    {
                        still.AddRect((float)x0, (float)y0, (float)x1, (float)y1);
                        any = true;
                    }
                    else
                    {
                        moving.Add((x0, y0, x1, y1, t, i, j));
                    }
                }

            if (any)
            {
                d.Save();
                d.ClipPath(still, antialias: false);
                DrawImage(d, f.A, cw, ch, 0, 0);
                d.Restore();
            }

            var lw = Math.Max(1, LwOf(f, 0.002));
            foreach (var m in moving)
            {
                var w = m.X1 - m.X0;
                var h = m.Y1 - m.Y0;
                var alpha = 1 - Smooth(0.7, 1, m.T);
                if (alpha <= 0.003) continue;

                var dx = Rs(seed, m.I, m.J, 4) * cw * 0.12 * m.T;
                var dy = (m.T * m.T * 1.25 - R(seed, m.I, m.J, 6) * 0.08 * m.T) * ch;
                var rot = Rs(seed, m.I, m.J, 5) * 70 * m.T;
                var s = 1 - 0.25 * m.T;
                d.Save();
                d.Translate((float)(m.X0 + w / 2 + dx), (float)(m.Y0 + h / 2 + dy));
                d.Rotate((float)rot);
                d.Scale((float)s, (float)s);
                PartImage(d, f.A, cw, ch, m.X0, m.Y0, w, h, -w / 2 - m.X0, -h / 2 - m.Y0, alpha);
                StrokeRect(d, -w / 2, -h / 2, w, h, Black, lw, 0.35);
                d.Restore();
            }
        },
    });

    private static void RegisterSliceShift() => Register(new TransitionTechnique
    {
        Id = "sliceShift",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 7));
            var vert = pars.Num("vert", 0) != 0;
            var l = vert ? cw : ch;
            var m2 = vert ? ch : cw;
            var lw = Math.Max(1, LwOf(f, 0.003));
            var ac = Accent(pars);

            for (var i = 0; i < n; i++)
            {
                var a0 = Math.Round(i * l / (double)n, MidpointRounding.AwayFromZero);
                var a1 = Math.Round((i + 1) * l / (double)n, MidpointRounding.AwayFromZero);
                var sg = i % 2 != 0 ? 1 : -1;
                var t = InOutCubic(Math.Clamp((p - i / (double)Math.Max(1, n - 1) * 0.3) / 0.7, 0, 1));
                var o = Math.Round(t * m2, MidpointRounding.AwayFromZero) * sg;
                if (vert)
                {
                    PartImage(d, f.A, cw, ch, a0, 0, a1 - a0, ch, 0, o);
                    PartImage(d, f.B, cw, ch, a0, 0, a1 - a0, ch, 0, o - sg * ch);
                }
                else
                {
                    PartImage(d, f.A, cw, ch, 0, a0, cw, a1 - a0, o, 0);
                    PartImage(d, f.B, cw, ch, 0, a0, cw, a1 - a0, o - sg * cw, 0);
                }
            }

            var alpha = Math.Pow(Bell(p), 0.7) * 0.9;
            for (var i = 1; i < n; i++)
            {
                var aq = Math.Round(i * l / (double)n, MidpointRounding.AwayFromZero);
                if (vert) Fill(d, aq - lw / 2, 0, lw, ch, ac, alpha);
                else Fill(d, 0, aq - lw / 2, cw, lw, ac, alpha);
            }
        },
    });

    private static void RegisterCubeTurn() => Register(new TransitionTechnique
    {
        Id = "cubeTurn",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var phi = InOutCubic(p) * Math.PI / 2;
            const double dd = 3.4;
            var ff = dd - 1;
            var cs = Math.Cos(phi);
            var sn = Math.Sin(phi);
            var dir = pars.Num("dir", 1);
            var bg = pars.Col("bg", "#000000");
            var prevBg = pars.Colors.ContainsKey("bgPrev") ? pars.Col("bgPrev", "#000000") : bg;
            var curBg = pars.Colors.ContainsKey("bgCur") ? pars.Col("bgCur", "#000000") : bg;

            Fill(d, 0, 0, cw, ch, Lerp(Lerp(prevBg, curBg, p), Black, 0.55), 1);

            (double X, double Y) Prj(double x, double z)
            {
                var xr = (x * cs - z * sn) * dir;
                var zr = x * sn + z * cs;
                var k = ff / (dd - zr);
                return (cw / 2.0 + xr * k * cw / 2, k * ch / 2);
            }

            void Face(ID2D1Bitmap1 img, double e00, double e01, double e10, double e11, double shade)
            {
                const int slices = 28;
                var q0 = Prj(e00, e01);
                var q1 = Prj(e10, e11);
                if ((q1.X - q0.X) * dir <= 0.5) return;

                var prev = q0;
                for (var k = 1; k <= slices; k++)
                {
                    var u = k / (double)slices;
                    var q = Prj(Lerp(e00, e10, u), Lerp(e01, e11, u));
                    var xa = Math.Min(prev.X, q.X);
                    var xb = Math.Max(prev.X, q.X);
                    var hh = (prev.Y + q.Y) / 2;
                    var su = dir > 0 ? (k - 1) / (double)slices : 1 - k / (double)slices;
                    Strip(d, img, cw, ch, slices, su, Math.Floor(xa), ch / 2.0 - hh,
                        Math.Ceiling(xb) - Math.Floor(xa) + 1, 2 * hh);
                    prev = q;
                }

                if (shade > 0.005)
                {
                    using var path = new Path2D(d.Factory)
                        .MoveTo((float)q0.X, (float)(ch / 2.0 - q0.Y))
                        .LineTo((float)q1.X, (float)(ch / 2.0 - q1.Y))
                        .LineTo((float)q1.X, (float)(ch / 2.0 + q1.Y))
                        .LineTo((float)q0.X, (float)(ch / 2.0 + q0.Y))
                        .Close();
                    d.FillPath(path, new Paint2D
                    {
                        Brush = new SolidBrush2D(Black),
                        Alpha = (float)shade,
                    });
                }
            }

            Face(f.A, -1, 1, 1, 1, 0.55 * (1 - cs));
            Face(f.B, 1, 1, 1, -1, 0.55 * (1 - sn));
        },
    });

    private static void RegisterHrStaticCut() => Register(new TransitionTechnique
    {
        Id = "hrStaticCut",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var step = (int)pars.Num("step", 3);
            var src = p < 0.5 ? f.A : f.B;
            var nz = p < 0.5 ? InQuad(p / 0.5) : 1 - OutQuad((p - 0.5) / 0.5);
            var oy = pars.Num("roll", 0) != 0
                ? (int)Math.Round(Math.Sin(p * Math.PI) * ch * 0.08 * (p < 0.5 ? 1 : -1))
                : 0;

            DrawImage(d, src, cw, ch, 0, oy);
            if (oy != 0) DrawImage(d, src, cw, ch, 0, oy > 0 ? oy - ch : oy + ch);

            var alpha = (float)Math.Min(1, nz * 1.15);
            for (var i = 0; i < 220; i++)
            {
                var v = (float)((40 + R(step, i, 4) * 215) / 255.0);
                var sz = 3 + R(step, i, 5) * 6;
                Fill(d, R(step, i, 6) * cw, R(step, i, 7) * ch, sz, sz,
                    new Color4(v, v, v, 1f), alpha);
            }

            Fill(d, 0, R(step, 3) * ch, cw, ch * 0.12, Black, 0.5 * nz);
        },
    });

    private static void RegisterHrBlink() => Register(new TransitionTechnique
    {
        Id = "hrBlink",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var src = p < 0.5 ? f.A : f.B;
            var c = p < 0.5 ? InCubic(p / 0.5) : 1 - OutCubic((p - 0.5) / 0.5);
            DrawImage(d, src, cw, ch, 0, 0);
            if (c <= 0) return;

            var h = ch / 2.0 * c * 1.02;
            var bow = ch * 0.18 * (1 - c * 0.6);
            using (var top = new Path2D(d.Factory)
                       .MoveTo(0, 0)
                       .LineTo(cw, 0)
                       .LineTo((float)cw, (float)(h - bow))
                       .QuadTo((float)(cw / 2.0), (float)(h + bow), 0, (float)(h - bow))
                       .Close())
                d.FillPath(top, new Paint2D { Brush = new SolidBrush2D(Black) });

            using (var bottom = new Path2D(d.Factory)
                       .MoveTo(0, ch)
                       .LineTo(cw, ch)
                       .LineTo((float)cw, (float)(ch - h + bow))
                       .QuadTo((float)(cw / 2.0), (float)(ch - h - bow), 0, (float)(ch - h + bow))
                       .Close())
                d.FillPath(bottom, new Paint2D { Brush = new SolidBrush2D(Black) });

            d.FillRect(new Rect(0, 0, cw, ch), new Paint2D
            {
                Alpha = (float)Math.Min(1, c),
                Brush = new LinearGradient2D(new Vector2(0, 0), new Vector2(0, ch), new[]
                {
                    new GradientStop2D(0f, new Color4(0f, 0f, 0f, 0.6f)),
                    new GradientStop2D(0.5f, new Color4(0f, 0f, 0f, 0f)),
                    new GradientStop2D(1f, new Color4(0f, 0f, 0f, 0.6f)),
                }),
            });
        },
    });

    private static void RegisterTyRuleWipe() => Register(new TransitionTechnique
    {
        Id = "tyRuleWipe",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 9));
            var bh = ch / (double)n;
            var rtl = pars.Num("rtl", 0) != 0;
            var ac = Accent(pars);
            var lw = Math.Max(1, Math.Min(cw, ch) * 0.0015);

            DrawImage(d, f.A, cw, ch, 0, 0);
            var st = 0.5 / n;
            for (var row = 0; row < n; row++)
            {
                var q = InOutCubic(Math.Clamp((p - row * st) / (1 - (n - 1) * st), 0, 1));
                var y0 = Math.Round(row * bh, MidpointRounding.AwayFromZero);
                var y1 = Math.Round((row + 1) * bh, MidpointRounding.AwayFromZero);
                var x = cw * q;
                if (q > 0)
                {
                    if (rtl) PartImage(d, f.B, cw, ch, cw - x, y0, x, y1 - y0);
                    else PartImage(d, f.B, cw, ch, 0, y0, x, y1 - y0);
                }

                if (q > 0 && q < 1)
                {
                    var cwid = Math.Max(3, bh * 0.14);
                    var xc = rtl ? cw - x - cwid : x;
                    Fill(d, xc, y0 + bh * 0.2, cwid, bh * 0.6, ac, 1);
                }
            }

            for (var row = 1; row < n; row++)
            {
                var y = Math.Round(row * bh, MidpointRounding.AwayFromZero) - lw / 2;
                Fill(d, 0, y, cw, lw, ac, 0.6 * Bell(p));
            }
        },
    });

    private static void RegisterTyGridCells() => Register(new TransitionTechnique
    {
        Id = "tyGridCells",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var rows = Math.Max(1, (int)pars.Num("rows", 5));
            var cell = ch / (double)rows;
            var cols = (int)Math.Ceiling(cw / cell);
            var ox = (cw - cols * cell) / 2;
            var total = rows * cols;
            var tate = pars.Num("tate", 1) != 0;
            var ac = Accent(pars);
            var lw = Math.Max(1, Math.Min(cw, ch) * 0.0016);

            DrawImage(d, f.A, cw, ch, 0, 0);
            var k = InOutCubic(Math.Clamp(p / 0.92, 0, 1)) * total;
            for (var j = 0; j < total; j++)
            {
                int c, r;
                if (tate)
                {
                    c = cols - 1 - j / rows;
                    r = j % rows;
                }
                else
                {
                    r = j / cols;
                    c = j % cols;
                }

                var x = ox + c * cell;
                var y = r * cell;
                if (j < Math.Floor(k))
                {
                    PartImage(d, f.B, cw, ch, x, y, cell + 1, cell + 1);
                }
                else if (j == Math.Floor(k))
                {
                    PartImage(d, f.B, cw, ch, x, y, cell + 1, cell + 1, 0, 0, k - j);
                    StrokeRect(d, x + lw * 1.5, y + lw * 1.5, cell - lw * 3, cell - lw * 3,
                        ac, lw * 3, 1);
                }
            }

            var lineAlpha = 0.5 * Bell(p);
            if (lineAlpha <= 0.003) return;
            for (var c = 0; c <= cols; c++)
            {
                var x = Math.Round(ox + c * cell, MidpointRounding.AwayFromZero) - lw / 2;
                Fill(d, x, 0, lw, ch, ac, lineAlpha);
            }

            for (var r = 1; r < rows; r++)
            {
                var y = Math.Round(r * cell, MidpointRounding.AwayFromZero) - lw / 2;
                Fill(d, 0, y, cw, lw, ac, lineAlpha);
            }
        },
    });

    private static void RegisterKnCornerSwing() => Register(new TransitionTechnique
    {
        Id = "knCornerSwing",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var c = (int)pars.Num("c", 0);
            var px = c == 1 || c == 2 ? cw : 0;
            var py = c >= 2 ? 0 : ch;
            var sg = c == 0 || c == 2 ? 1 : -1;
            var e = InOutCubic(p);

            Fill(d, 0, 0, cw, ch, Bg(pars), 1);

            void Put(ID2D1Bitmap1 img, double ang)
            {
                d.Save();
                d.Translate((float)px, (float)py);
                d.Rotate((float)ang);
                d.Translate(-px, -py);
                d.DrawBitmap(img, new Rect(0, 0, cw, ch));
                d.Restore();
            }

            Put(f.B, -90 * sg * (1 - e));
            Put(f.A, 90 * sg * e);

            var lw = Math.Max(2, MinD(f) * 0.006);
            d.Save();
            d.Translate((float)px, (float)py);
            d.Rotate((float)(90 * sg * e));
            var y0 = -(py != 0 ? ch : 0);
            if (c == 0 || c == 3) Fill(d, -lw, y0, lw, ch * 2.0, Accent(pars), Bell(p));
            else Fill(d, 0, y0, lw, ch * 2.0, Accent(pars), Bell(p));
            d.Restore();
        },
    });

    private static void RegisterKnStutterCut() => Register(new TransitionTechnique
    {
        Id = "knStutterCut",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var z = pars.Num("z", 1.07);
            var o = pars.Num("o", 0.03) * cw;
            var seq = new (ID2D1Bitmap1 Img, double S, double Dx)[]
            {
                (f.A, 1.0, 0),
                (f.B, z, o),
                (f.A, 1 / z, -o * 0.6),
                (f.B, z * 1.03, -o),
                (f.B, 1.0, 0),
            };
            var cuts = new[] { 0.18, 0.36, 0.52, 0.7 };
            var k = 0;
            while (k < cuts.Length && p >= cuts[k]) k++;
            var (img, s, dx) = seq[k];

            Fill(d, 0, 0, cw, ch, Bg(pars), 1);
            var dx0 = cw / 2.0 - cw * s / 2 + dx;
            var dy0 = ch / 2.0 - ch * s / 2;
            d.DrawBitmap(img, new Rect((float)dx0, (float)dy0, (float)(cw * s), (float)(ch * s)));

            var since = k > 0 ? p - cuts[k - 1] : 1;
            if (since < 0.06)
            {
                var h = Math.Max(3, ch * 0.012);
                Fill(d, 0, (k % 2 != 0 ? 0.3 : 0.68) * ch, cw, h, Accent(pars), 0.9);
            }
        },
    });

    private static void RegisterKnStripSlam() => Register(new TransitionTechnique
    {
        Id = "knStripSlam",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var n = Math.Max(1, (int)pars.Num("n", 4));
            var rev = pars.Num("rev", 0) != 0;
            var up = pars.Num("up", 0) != 0;

            DrawImage(d, f.A, cw, ch, 0, 0);
            Fill(d, 0, 0, cw, ch, Black, 0.3 * p);

            for (var idx = 0; idx < n; idx++)
            {
                var kk = rev ? n - 1 - idx : idx;
                var delay = kk / (double)n * 0.45;
                var q = Math.Clamp((p - delay) / 0.55, 0, 1);
                if (q <= 0) continue;

                const double tl = 0.62;
                double y;
                if (q < tl)
                {
                    var fr = q / tl;
                    y = -ch * (1 - fr * fr);
                }
                else
                {
                    var t = (q - tl) / (1 - tl);
                    y = -ch * 0.06 * Math.Abs(Math.Sin(t * Math.PI * 2)) * (1 - t);
                }

                if (up) y = -y;
                var x0 = (int)Math.Round(idx * cw / (double)n);
                var x1 = (int)Math.Round((idx + 1) * cw / (double)n);
                var ry = Math.Round(y);
                PartImage(d, f.B, cw, ch, x0, 0, x1 - x0, ch, 0, ry);
                if (q < 1)
                {
                    var by = up ? ry - 3 : ry + ch - 3;
                    Fill(d, x0, by, x1 - x0, Math.Max(3, ch * 0.008), Accent(pars), 0.8 * (1 - q));
                }
            }
        },
    });

    private static void RegisterChannelSwitch() => Register(new TransitionTechnique
    {
        Id = "channelSwitch",
        Blend = (d, f, p, pars) =>
        {
            var cw = f.Width;
            var ch = f.Height;
            var seed = pars.Seed ^ (int)pars.Num("seed", 1);
            var step = (int)pars.Num("step", 3);
            var src = p < 0.5 ? f.A : f.B;
            var kp = p < 0.5 ? p * 2 : (p - 0.5) * 2;
            var roll = (int)Math.Floor(kp * 1.3 % 1 * ch);

            DrawImage(d, src, cw, ch, 0, roll - ch);
            DrawImage(d, src, cw, ch, 0, roll);

            for (var i = 0; i < 4; i++)
            {
                var yy = ch * R(seed, step, i, 1);
                var hh = ch * (0.02 + 0.06 * R(seed, i, 2));
                var dx = Rs(seed, step, i, 3) * cw * 0.08 * (0.4 + kp);
                if (Math.Abs(dx) > 1) PartImage(d, src, cw, ch, 0, yy, cw, hh, dx, 0);
            }

            var burst = p > 0.3 && p < 0.7 ? 1 - Math.Abs(p - 0.5) / 0.2 : 0;
            if (burst > 0.03)
            {
                var w = cw * 0.62 * burst;
                var h = ch * 0.5 * burst;
                var bx = (cw - w) / 2;
                var by = (ch - h) / 2;
                d.Save();
                d.ClipRect(new Rect((float)bx, (float)by, (float)w, (float)h), antialias: false);
                Fill(d, bx, by, w, h, new Color4(8f / 255f, 8f / 255f, 8f / 255f, 1f), 1);
                for (var i = 0; i < 220; i++)
                {
                    var v = (float)((40 + R(seed, step, i, 4) * 215) / 255.0);
                    var sz = 3 + R(seed, step, i, 5) * 6;
                    Fill(d, bx + R(seed, step, i, 6) * (w - sz), by + R(seed, step, i, 7) * (h - sz),
                        sz, sz, new Color4(v, v, v, 1f), 1);
                }

                d.Restore();
            }

            Fill(d, 0, roll - ch * 0.012, cw, ch * 0.024, White, 0.14 + burst * 0.2);
            for (var yy = step * 7 % 6; yy < ch; yy += 6)
                Fill(d, 0, yy, cw, 1, Black, 0.06 * kp);
        },
    });

    private static readonly Color4 Black = new(0f, 0f, 0f, 1f);
    private static readonly Color4 White = new(1f, 1f, 1f, 1f);

    private static Color4 Accent(TransitionParams pars) => pars.Col("accent", "#FFFFFF");

    private static Color4 Accent2(TransitionParams pars) => pars.Col("accent2", "#FFFFFF");

    private static Color4 Bg(TransitionParams pars) => pars.Col("bg", "#000000");

    private static double Lum(Color4 c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static double MinD(in TransitionFrame f) => Math.Min(f.Width, f.Height);

    private static double LwOf(in TransitionFrame f, double k) => Math.Max(2, MinD(f) * k);

    private static double Bell(double k) => Math.Sin(Math.PI * Math.Clamp(k, 0, 1));

    private static double InQuad(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x;
    }

    private static double InCubic(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * x;
    }

    private static double OutCubic(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return 1 - Math.Pow(1 - x, 3);
    }

    private static double InOutCubic(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    private static double Smooth(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Mirror(bool rev, int cw, double v) => rev ? cw - v : v;

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Color4 Lerp(Color4 a, Color4 b, double t) => new(
        (float)(a.R + (b.R - a.R) * t),
        (float)(a.G + (b.G - a.G) * t),
        (float)(a.B + (b.B - a.B) * t),
        (float)(a.A + (b.A - a.A) * t));

    private static double Rs(int a, int b, int c, int d = 0) => R(a, b, c, d) * 2 - 1;

    private static double IoQuart(double k)
    {
        k = Math.Clamp(k, 0, 1);
        return k < 0.5 ? 8 * k * k * k * k : 1 - 8 * Math.Pow(1 - k, 4);
    }

    private static double InOutSine(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return -(Math.Cos(Math.PI * x) - 1) / 2;
    }

    private static double OutQuad(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return 1 - (1 - x) * (1 - x);
    }

    private static void DrawImage(Immediate2D d, ID2D1Bitmap1 img, int cw, int ch, double x, double y,
        double alpha = 1, bool smooth = true)
    {
        if (alpha <= 0.003) return;
        d.DrawBitmap(img, new Rect((float)x, (float)y, cw, ch), (float)Math.Min(1, alpha),
            smooth ? BitmapInterpolationMode.Linear : BitmapInterpolationMode.NearestNeighbor);
    }

    private static void PartImage(Immediate2D d, ID2D1Bitmap1 img, int cw, int ch,
        double x, double y, double w, double h, double dx = 0, double dy = 0, double alpha = 1)
    {
        if (alpha <= 0.003) return;
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(cw, (int)Math.Ceiling(x + w));
        var y1 = Math.Min(ch, (int)Math.Ceiling(y + h));
        if (x1 - x0 < 1 || y1 - y0 < 1) return;

        d.Save();
        d.ClipRect(new Rect((float)(x0 + dx), (float)(y0 + dy), x1 - x0, y1 - y0), antialias: false);
        d.DrawBitmap(img, new Rect((float)dx, (float)dy, cw, ch), (float)Math.Min(1, alpha),
            BitmapInterpolationMode.NearestNeighbor);
        d.Restore();
    }

    private static void ScaledImage(Immediate2D d, ID2D1Bitmap1 img, int cw, int ch, double s,
        double alpha = 1, bool smooth = true)
    {
        if (alpha <= 0.003) return;
        var x = cw * 0.5 * (1 - s);
        var y = ch * 0.5 * (1 - s);
        d.DrawBitmap(img, new Rect((float)x, (float)y, (float)(cw * s), (float)(ch * s)),
            (float)Math.Min(1, alpha),
            smooth ? BitmapInterpolationMode.Linear : BitmapInterpolationMode.NearestNeighbor);
    }

    private static void PixelPass(Immediate2D d, ID2D1Bitmap1 img, int cw, int ch, double maxB,
        double t, double alpha)
    {
        var bs = 1 + (maxB - 1) * t;
        if (alpha <= 0.003) return;
        if (bs < 1.6)
        {
            DrawImage(d, img, cw, ch, 0, 0, alpha);
            return;
        }

        var w = Math.Max(1, (int)Math.Ceiling(cw / bs));
        var h = Math.Max(1, (int)Math.Ceiling(ch / bs));
        d.Save();
        d.Scale((float)bs, (float)bs);
        d.PushSurface(w, h, clear: true);
        d.DrawBitmap(img, new Rect(0, 0, w, h), 1f);
        d.PopSurface(new Paint2D { Alpha = (float)Math.Min(1, alpha) });
        d.Restore();
    }

    private static void Fill(Immediate2D d, double x, double y, double w, double h, Color4 color,
        double alpha = 1)
    {
        if (alpha <= 0.003 || w <= 0 || h <= 0) return;
        d.FillRect(new Rect((float)x, (float)y, (float)w, (float)h), new Paint2D
        {
            Brush = new SolidBrush2D(color),
            Alpha = (float)Math.Min(1, alpha),
        });
    }

    private static void StrokeLine(Immediate2D d, double x0, double y0, double x1, double y1,
        Color4 color, double lw, double alpha)
    {
        if (alpha <= 0.003) return;
        d.DrawLine((float)x0, (float)y0, (float)x1, (float)y1, new Paint2D
        {
            Brush = new SolidBrush2D(color),
            Alpha = (float)Math.Min(1, alpha),
            StrokeWidth = (float)lw,
            StrokeCap = StrokeCap2D.Round,
        });
    }

    private static void StrokeCircle(Immediate2D d, double cx, double cy, double r, Color4 color,
        double lw, double alpha)
    {
        if (alpha <= 0.003 || r <= 0) return;
        d.StrokeCircle((float)cx, (float)cy, (float)r, new Paint2D
        {
            Brush = new SolidBrush2D(color),
            Alpha = (float)Math.Min(1, alpha),
            StrokeWidth = (float)lw,
            IsStroke = true,
        });
    }

    private static void StrokeRect(Immediate2D d, double x, double y, double w, double h,
        Color4 color, double lw, double alpha)
    {
        if (alpha <= 0.003 || w <= 0 || h <= 0) return;
        d.StrokeRect(new Rect((float)x, (float)y, (float)w, (float)h), new Paint2D
        {
            Brush = new SolidBrush2D(color),
            Alpha = (float)Math.Min(1, alpha),
            StrokeWidth = (float)lw,
            IsStroke = true,
        });
    }

    private static void Echo(Immediate2D d, ID2D1Bitmap1 img, int cw, int ch, int w, int h,
        double o, bool vert, double alpha)
    {
        if (vert)
            d.DrawBitmap(img, new Rect(0, (float)(o * h / ch), w, h), (float)Math.Min(1, alpha),
                BitmapInterpolationMode.Linear);
        else
            d.DrawBitmap(img, new Rect((float)(o * w / cw), 0, w, h), (float)Math.Min(1, alpha),
                BitmapInterpolationMode.Linear);
    }

    private static void Strip(Immediate2D d, ID2D1Bitmap1 img, int cw, int ch, int slices,
        double su, double x, double y, double w, double h)
    {
        if (w < 1 || h < 1) return;
        var srcW = cw / (double)slices;
        var sx = w / srcW;
        var sy = h / ch;
        d.Save();
        d.ClipRect(new Rect((float)x, (float)y, (float)w, (float)h), antialias: false);
        d.Translate((float)(x - su * srcW * sx), (float)y);
        d.Scale((float)sx, (float)sy);
        d.DrawBitmap(img, new Rect(0, 0, cw, ch), 1f, BitmapInterpolationMode.NearestNeighbor);
        d.Restore();
    }

    private static Path2D BlobPath(Immediate2D d, double cx, double cy, double r, int seed, double ph,
        int n = 56)
    {
        var pts = new (double X, double Y)[n];
        for (var i = 0; i < n; i++)
        {
            var ang = i / (double)n * Tau;
            var w = 1 + 0.13 * Math.Sin(ang * 3 + R(seed, 1) * 6 + ph)
                      + 0.08 * Math.Sin(ang * 5 + R(seed, 2) * 6 - ph * 1.3)
                      + 0.05 * Math.Sin(ang * 9 + R(seed, 3) * 6 + ph * 0.7);
            pts[i] = (cx + Math.Cos(ang) * r * w, cy + Math.Sin(ang) * r * w);
        }

        (double X, double Y) Mid(int idx) =>
            ((pts[idx % n].X + pts[(idx + 1) % n].X) / 2, (pts[idx % n].Y + pts[(idx + 1) % n].Y) / 2);

        var path = new Path2D(d.Factory);
        var m0 = Mid(0);
        path.MoveTo((float)m0.X, (float)m0.Y);
        for (var i = 1; i <= n; i++)
        {
            var q = pts[i % n];
            var m = Mid(i);
            path.QuadTo((float)q.X, (float)q.Y, (float)m.X, (float)m.Y);
        }

        return path.Close();
    }

    private static void ShadowBand(Immediate2D d, bool horizontal, int cw, int ch, double edge,
        double dSw, double alpha)
    {
        if (alpha <= 0.003 || Math.Abs(dSw) < 0.5) return;

        Vector2 g0, g1;
        Rect rect;
        if (horizontal)
        {
            g0 = new Vector2((float)edge, 0);
            g1 = new Vector2((float)(edge + dSw), 0);
            rect = new Rect((float)(dSw > 0 ? edge : edge + dSw), 0, (float)Math.Abs(dSw), ch);
        }
        else
        {
            g0 = new Vector2(0, (float)edge);
            g1 = new Vector2(0, (float)(edge + dSw));
            rect = new Rect(0, (float)(dSw > 0 ? edge : edge + dSw), cw, (float)Math.Abs(dSw));
        }

        d.FillRect(rect, new Paint2D
        {
            Brush = new LinearGradient2D(g0, g1, new[]
            {
                new GradientStop2D(0f, new Color4(0f, 0f, 0f, (float)Math.Min(1, alpha))),
                new GradientStop2D(1f, new Color4(0f, 0f, 0f, 0f)),
            }),
        });
    }

    private static Path2D Quad(Immediate2D d, double x0, double x1, double sl, int ch) =>
        new Path2D(d.Factory)
            .MoveTo((float)(x0 - sl / 2), 0)
            .LineTo((float)(x1 - sl / 2), 0)
            .LineTo((float)(x1 + sl / 2), ch)
            .LineTo((float)(x0 + sl / 2), ch)
            .Close();

    private static void FillQuad(Immediate2D d, double x0, double x1, double sl, int ch,
        Color4 color, double alpha)
    {
        if (alpha <= 0.003) return;
        using var path = Quad(d, x0, x1, sl, ch);
        d.FillPath(path, new Paint2D
        {
            Brush = new SolidBrush2D(color),
            Alpha = (float)Math.Min(1, alpha),
        });
    }

    private static Path2D Pie(Immediate2D d, double cx, double cy, double r, double a0, double a1)
    {
        var path = new Path2D(d.Factory).MoveTo((float)cx, (float)cy);
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(a1 - a0) / (Math.PI / 64)));
        for (var i = 0; i <= steps; i++)
        {
            var a = a0 + (a1 - a0) * i / steps;
            path.LineTo((float)(cx + Math.Cos(a) * r), (float)(cy + Math.Sin(a) * r));
        }

        return path.Close();
    }

    private static double R(int a, int b = 0, int c = 0, int d = 0, int e = 0) => H(a, b, c, d, e) / 4294967296.0;

    private static uint H(int a, int b = 0, int c = 0, int d = 0, int e = 0)
    {
        unchecked
        {
            var h = (int)(0x9e3779b9 ^ a);
            h = Imul(h ^ (int)((uint)h >> 16), unchecked((int)0x85ebca6b));
            h += Imul((int)((uint)b + 0x632be5ab), unchecked((int)0xc2b2ae35));
            h = Imul(h ^ (int)((uint)h >> 13), unchecked((int)0xc2b2ae35));
            h += Imul((int)((uint)c + 0x5bd1e995), unchecked((int)0x27d4eb2f));
            h = Imul(h ^ (int)((uint)h >> 15), unchecked((int)0x165667b1));
            h += Imul((int)((uint)d + 0x1b873593), unchecked((int)0x85ebca6b));
            h = Imul(h ^ (int)((uint)h >> 16), unchecked((int)0x27d4eb2f));
            h += Imul((int)((uint)e + 0x68e31da4), unchecked((int)0x9e3779b1));
            h ^= (int)((uint)h >> 15);
            h = Imul(h, unchecked((int)0x2c1b3c6d));
            h ^= (int)((uint)h >> 12);
            h = Imul(h, unchecked((int)0x297a2d39));
            h ^= (int)((uint)h >> 15);
            return (uint)h;
        }
    }

    private static int Imul(int a, int b) => unchecked(a * b);
}
