using System.Numerics;
using JizuraLive.LegacyEngine;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public readonly record struct PvAvoidImage(int Width, int Height, byte[]? Alpha, int AlphaWidth, int AlphaHeight)
{
    public static PvAvoidImage From(PvLayerImage image) =>
        new(image.Width, image.Height, image.Alpha, image.AlphaWidth, image.AlphaHeight);
}

public sealed record PvAvoidSnapshot(
    int Width,
    int Height,
    PvCameraConfig Camera,
    DirectorEvaluator? Curves,
    PvLayerConfig? Character,
    PvLayerConfig? LegacyFront,
    PvLayerConfig? Background,
    PvAvoidImage CharacterImage,
    PvAvoidImage BackgroundImage,
    PvVtsControl Vts,
    bool StageCamera,
    bool CameraAffectsCharacter,
    bool GlobalParallax,
    bool AntiBleed);

public static class PvAvatarAvoid
{
    private const double EdgeInset = 0.02;
    private const double MinBand = 0.12;
    private const double BlockPad = 0.03;
    private const int GridX = 32;
    private const int GridY = 18;

    public static PvLayerConfig? FindBackground(PvScene scene,
        IReadOnlyDictionary<string, PvLayerImage>? images = null)
    {
        var layers = scene.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layer.Enabled || layer.Kind != PvLayerKind.Background) continue;
            if (scene.IsCharacterLayer(layer)) continue;
            if (images is not null
                && (!images.TryGetValue(layer.Id, out var image) || !image.HasContent)) continue;
            return layer;
        }

        return null;
    }

    public static PvAvoidSnapshot? Snapshot(PvScene scene, DirectorEvaluator? curves, PvLayerConfig? character,
        PvLayerConfig? legacyFront, PvLayerConfig? background, PvLayerImage characterImage, PvLayerImage backgroundImage,
        PvVtsControl vts, bool stageCamera)
    {
        if (character is null || legacyFront is null) return null;

        var camera = scene.Camera ?? new PvCameraConfig();
        return new PvAvoidSnapshot(
            scene.Width,
            scene.Height,
            new PvCameraConfig { X = camera.X, Y = camera.Y, Scale = camera.Scale, Rotation = camera.Rotation },
            curves,
            character,
            legacyFront,
            background,
            PvAvoidImage.From(characterImage),
            PvAvoidImage.From(backgroundImage),
            vts,
            stageCamera,
            scene.CameraAffectsCharacter,
            scene.GlobalParallaxEnabled,
            scene.AntiBleedGuardEnabled);
    }

    public static IReadOnlyList<Zone> Zones(PvAvoidSnapshot snapshot, double start, double end,
        int planWidth, int planHeight)
    {
        if (snapshot.Character is null || snapshot.LegacyFront is null) return Array.Empty<Zone>();
        if (snapshot.Width <= 0 || snapshot.Height <= 0) return Array.Empty<Zone>();
        if (!double.IsFinite(start) || !double.IsFinite(end) || end - start <= 0.01) return Array.Empty<Zone>();

        try
        {
            return Compute(snapshot, start, end, planWidth, planHeight);
        }
        catch
        {
            return Array.Empty<Zone>();
        }
    }

    private static IReadOnlyList<Zone> Compute(PvAvoidSnapshot snapshot, double start, double end,
        int planWidth, int planHeight)
    {
        var w = snapshot.Width;
        var h = snapshot.Height;
        var character = snapshot.Character;
        var front = snapshot.LegacyFront;
        if (character is null || front is null || w <= 0 || h <= 0) return Array.Empty<Zone>();

        var characterImage = ImageOf(snapshot.CharacterImage, w, h);
        var content = PvStageCompositor.FitRect(characterImage, character.Cover, w, h);
        var alpha = snapshot.CharacterImage;
        var vts = snapshot.Vts;
        var hasHullPoints = vts.HasHull && vts.HullPoints is { Length: >= 6 };
        var useHullBox = vts.HasHull && !hasHullPoints;
        var alphaReady = alpha.Alpha is { Length: > 0 } && alpha.AlphaWidth > 0 && alpha.AlphaHeight > 0;

        double[]? hullTest = null;
        var hullInUv = false;
        if (hasHullPoints)
        {
            hullTest = vts.HullPoints;
            if (alphaReady && AlphaUvBounds(alpha.Alpha!, alpha.AlphaWidth, alpha.AlphaHeight) is { } uv)
            {
                hullTest = MapHullToUv(hullTest!, vts, uv);
                hullInUv = true;
            }
        }

        var useGrid = hullTest is not null || (!useHullBox && alphaReady);
        var grid = useGrid ? new bool[GridX * GridY] : null;

        var samples = Math.Clamp((int)Math.Ceiling((end - start) / 0.3), 3, 12);
        var middle = samples / 2;
        Rect? window = null;
        Rect? blocked = null;

        for (var i = 0; i < samples; i++)
        {
            var t = start + (end - start) * i / (samples - 1);
            var cam = snapshot.StageCamera
                ? PvPipeline.EvaluateCamera(snapshot.Camera, w, h, snapshot.Curves, t, snapshot.Background,
                    BackgroundState(snapshot, t), ImageOf(snapshot.BackgroundImage, w, h), snapshot.GlobalParallax)
                : PvCamState.Identity;

            var frontState = PvPipeline.EvaluateLayer(front, snapshot.Curves, t);
            var frontTf = PvParallax.Compute(w, h, front, frontState, cam,
                globalParallax: snapshot.GlobalParallax, antiBleed: snapshot.AntiBleed);
            if (!Matrix3x2.Invert(frontTf.Matrix, out var inv)) return Array.Empty<Zone>();
            if (i == middle) window = MapBounds(inv, CanvasCorners(w, h));

            var characterCam = PvPipeline.CharacterCameraState(character, cam, snapshot.Curves, t,
                snapshot.CameraAffectsCharacter);
            var characterState = PvPipeline.EvaluateLayer(character, snapshot.Curves, t)
                with { CameraOverride = characterCam };
            var quad = CharacterQuad(snapshot, character, characterState, characterCam, w, h);
            var local = MapBounds(inv, quad);
            blocked = blocked is { } box ? Union(box, local) : local;

            if (grid is null) continue;
            var charTf = PvParallax.Compute(w, h, character, characterState, characterCam,
                globalParallax: snapshot.GlobalParallax, antiBleed: snapshot.AntiBleed,
                parallaxOverride: snapshot.CameraAffectsCharacter ? PvParallax.FullFollow : null);
            MarkGrid(grid, alpha, content, frontTf.Matrix, charTf.Matrix, w, h, hullTest, hullInUv, vts);
        }

        if (window is not { } area) return Array.Empty<Zone>();

        var sx = planWidth / (double)w;
        var sy = planHeight / (double)h;
        if (grid is not null && (hullTest is not null ? AnyBlocked(grid) : AlphaUsable(grid)))
        {
            var free = LargestFree(grid, area, w, h);
            return free is { } rect ? new[] { ToZone(rect, area, sx, sy) } : Array.Empty<Zone>();
        }

        if (blocked is not { } blockedBox) return Array.Empty<Zone>();

        var pad = BlockPad * Math.Min(w, h);
        blockedBox = new Rect(blockedBox.X - (float)pad, blockedBox.Y - (float)pad,
            blockedBox.Width + (float)pad * 2f, blockedBox.Height + (float)pad * 2f);

        var insetX = (float)(EdgeInset * w);
        var insetY = (float)(EdgeInset * h);
        var usable = Intersect(area, new Rect(insetX, insetY, w - insetX * 2f, h - insetY * 2f));
        if (usable.Width <= 0 || usable.Height <= 0) return Array.Empty<Zone>();

        var bands = new List<(Rect R, string Side)>(4);
        AddBand(bands, usable, blockedBox, "left",
            new Rect(usable.X, usable.Y, blockedBox.X - usable.X, usable.Height));
        AddBand(bands, usable, blockedBox, "right",
            new Rect(blockedBox.X + blockedBox.Width, usable.Y,
                usable.X + usable.Width - blockedBox.X - blockedBox.Width, usable.Height));
        AddBand(bands, usable, blockedBox, "top",
            new Rect(usable.X, usable.Y, usable.Width, blockedBox.Y - usable.Y));
        AddBand(bands, usable, blockedBox, "bottom",
            new Rect(usable.X, blockedBox.Y + blockedBox.Height, usable.Width,
                usable.Y + usable.Height - blockedBox.Y - blockedBox.Height));

        bands.Sort((a, b) => (b.R.Width * b.R.Height).CompareTo(a.R.Width * a.R.Height));
        return bands.Count == 0
            ? Array.Empty<Zone>()
            : new[] { ToZone(bands[0].R, area, sx, sy, bands[0].Side) };
    }

    private static Zone ToZone(Rect rect, Rect area, double sx, double sy, string? side = null)
    {
        side ??= rect.Width >= rect.Height
            ? rect.X + rect.Width / 2f < area.X + area.Width / 2f ? "left" : "right"
            : rect.Y + rect.Height / 2f < area.Y + area.Height / 2f ? "top" : "bottom";
        return new Zone
        {
            X = (int)Math.Round(rect.X * sx),
            Y = (int)Math.Round(rect.Y * sy),
            W = Math.Max(1, (int)Math.Round(rect.Width * sx)),
            H = Math.Max(1, (int)Math.Round(rect.Height * sy)),
            Side = side,
        };
    }

    private static void MarkGrid(bool[] grid, in PvAvoidImage image, Rect content, Matrix3x2 front,
        Matrix3x2 character, int w, int h, double[]? hullTest, bool hullInUv, in PvVtsControl vts)
    {
        if (!Matrix3x2.Invert(character, out var inv)) return;

        byte[]? alpha = null;
        if (hullTest is null && image.Alpha is { Length: > 0 } pixels
            && image.AlphaWidth > 0 && image.AlphaHeight > 0)
            alpha = pixels;
        if (hullTest is null && alpha is null) return;

        var halfW = Math.Max(0.001f, content.Width / 2f);
        var halfH = Math.Max(0.001f, content.Height / 2f);
        var cx = content.X + halfW;
        var cy = content.Y + halfH;
        var aw = image.AlphaWidth;
        var ah = image.AlphaHeight;

        for (var gy = 0; gy < GridY; gy++)
        {
            var ly = (gy + 0.5f) * h / GridY;
            for (var gx = 0; gx < GridX; gx++)
            {
                if (grid[gy * GridX + gx]) continue;
                var lx = (gx + 0.5f) * w / GridX;
                var screen = Vector2.Transform(new Vector2(lx, ly), front);
                var local = Vector2.Transform(screen, inv);
                var u = (local.X - content.X) / Math.Max(0.001f, content.Width);
                var v = (local.Y - content.Y) / Math.Max(0.001f, content.Height);

                if (hullTest is not null)
                {
                    if (hullInUv)
                    {
                        if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                        if (InsideHull(hullTest, u, v)) grid[gy * GridX + gx] = true;
                        continue;
                    }

                    double nx = (local.X - cx) / halfW;
                    double ny = (cy - local.Y) / halfH;
                    if (nx < vts.HullMinX || nx > vts.HullMaxX
                        || ny < vts.HullMinY || ny > vts.HullMaxY) continue;
                    if (InsideHull(hullTest, nx, ny)) grid[gy * GridX + gx] = true;
                    continue;
                }

                if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                var ax = Math.Clamp((int)(u * (aw - 1)), 0, aw - 1);
                var ay = Math.Clamp((int)(v * (ah - 1)), 0, ah - 1);
                if (alpha![ay * aw + ax] > 8) grid[gy * GridX + gx] = true;
            }
        }
    }

    private readonly record struct UvRect(double U0, double V0, double U1, double V1);

    private static UvRect? AlphaUvBounds(byte[] alpha, int aw, int ah)
    {
        int minX = aw, minY = ah, maxX = -1, maxY = -1;
        for (var y = 0; y < ah; y++)
        {
            var row = y * aw;
            for (var x = 0; x < aw; x++)
            {
                if (alpha[row + x] <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY) return null;
        var w = (double)Math.Max(1, aw - 1);
        var h = (double)Math.Max(1, ah - 1);
        return new UvRect(minX / w, minY / h, (maxX + 1) / w, (maxY + 1) / h);
    }

    private static double[] MapHullToUv(double[] points, in PvVtsControl vts, UvRect uv)
    {
        var n = points.Length / 2;
        var result = new double[points.Length];
        var dx = Math.Max(1e-6, vts.HullMaxX - vts.HullMinX);
        var dy = Math.Max(1e-6, vts.HullMaxY - vts.HullMinY);
        for (var i = 0; i < n; i++)
        {
            var nx = (points[i * 2] - vts.HullMinX) / dx;
            var ny = (points[i * 2 + 1] - vts.HullMinY) / dy;
            result[i * 2] = uv.U0 + nx * (uv.U1 - uv.U0);
            result[i * 2 + 1] = uv.V1 - ny * (uv.V1 - uv.V0);
        }

        return result;
    }

    private static bool InsideHull(double[] points, double x, double y)
    {
        var n = points.Length / 2;
        var sign = 0;
        for (var i = 0; i < n; i++)
        {
            var j = i + 1 == n ? 0 : i + 1;
            var x0 = points[i * 2];
            var y0 = points[i * 2 + 1];
            var x1 = points[j * 2];
            var y1 = points[j * 2 + 1];
            var cross = (x1 - x0) * (y - y0) - (y1 - y0) * (x - x0);
            if (cross > 1e-9)
            {
                if (sign < 0) return false;
                sign = 1;
            }
            else if (cross < -1e-9)
            {
                if (sign > 0) return false;
                sign = -1;
            }
        }

        return true;
    }

    private static bool AnyBlocked(bool[] grid)
    {
        foreach (var cell in grid)
            if (cell) return true;
        return false;
    }

    private static bool AlphaUsable(bool[] grid)
    {
        var blocked = 0;
        foreach (var cell in grid)
            if (cell) blocked++;
        return blocked > 0 && blocked < grid.Length * 0.985;
    }

    private static Rect? LargestFree(bool[] grid, Rect window, int w, int h)
    {
        var insetX = (float)(EdgeInset * w);
        var insetY = (float)(EdgeInset * h);
        var usable = Intersect(window, new Rect(insetX, insetY, w - insetX * 2f, h - insetY * 2f));
        if (usable.Width <= 0 || usable.Height <= 0) return null;

        var blocked = new bool[grid.Length];
        Array.Copy(grid, blocked, grid.Length);
        for (var gy = 0; gy < GridY; gy++)
        {
            var cy = (gy + 0.5f) * h / GridY;
            for (var gx = 0; gx < GridX; gx++)
            {
                var cx = (gx + 0.5f) * w / GridX;
                if (cx < usable.X || cx > usable.X + usable.Width
                    || cy < usable.Y || cy > usable.Y + usable.Height)
                    blocked[gy * GridX + gx] = true;
            }
        }

        var dilated = new bool[grid.Length];
        for (var gy = 0; gy < GridY; gy++)
        {
            for (var gx = 0; gx < GridX; gx++)
            {
                if (!blocked[gy * GridX + gx]) continue;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var x = gx + dx;
                        var y = gy + dy;
                        if (x >= 0 && x < GridX && y >= 0 && y < GridY) dilated[y * GridX + x] = true;
                    }
                }
            }
        }

        int bestW = 0, bestH = 0, bestX = 0, bestY = 0;
        var free = new bool[GridX];
        for (var y0 = 0; y0 < GridY; y0++)
        {
            Array.Fill(free, true);
            for (var y1 = y0; y1 < GridY; y1++)
            {
                for (var gx = 0; gx < GridX; gx++) free[gx] &= !dilated[y1 * GridX + gx];
                var run = 0;
                for (var gx = 0; gx <= GridX; gx++)
                {
                    if (gx < GridX && free[gx])
                    {
                        run++;
                        continue;
                    }

                    if (run == 0) continue;
                    var height = y1 - y0 + 1;
                    if (run * height > bestW * bestH)
                    {
                        bestW = run;
                        bestH = height;
                        bestX = gx - run;
                        bestY = y0;
                    }

                    run = 0;
                }
            }
        }

        if (bestW < 2 || bestH < 2) return null;
        return new Rect(bestX * (float)w / GridX, bestY * (float)h / GridY,
            bestW * (float)w / GridX, bestH * (float)h / GridY);
    }

    private static void AddBand(List<(Rect R, string Side)> bands, Rect usable, Rect block, string side,
        Rect candidate)
    {
        var rect = Intersect(candidate, usable);
        if (rect.Width < MinBand * usable.Width || rect.Height < MinBand * usable.Height) return;
        if (Intersects(rect, block)) return;
        bands.Add((rect, side));
    }

    private static PvLayerState BackgroundState(PvAvoidSnapshot snapshot, double time)
    {
        if (snapshot.Background is null) return default;

        var state = PvPipeline.EvaluateLayer(snapshot.Background, snapshot.Curves, time);
        if (snapshot.Curves is not null
            && snapshot.Curves.TryLayerCameraAt(snapshot.Background.Id, time, out var layerCam))
            state = PvPipeline.ApplyLayerCamera(state, layerCam, snapshot.Width, snapshot.Height);
        return state;
    }

    private static PvLayerImage ImageOf(in PvAvoidImage image, int fallbackWidth, int fallbackHeight)
    {
        var width = image.Width > 0 ? image.Width : fallbackWidth;
        var height = image.Height > 0 ? image.Height : fallbackHeight;
        return new PvLayerImage(null, new Color4(0f, 0f, 0f, 1f), width, height)
        {
            Alpha = image.Alpha,
            AlphaWidth = image.AlphaWidth,
            AlphaHeight = image.AlphaHeight,
        };
    }

    private static Vector2[] CanvasCorners(int w, int h) =>
        [new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, h), new Vector2(0, h)];

    private static Vector2[] CharacterQuad(PvAvoidSnapshot snapshot, PvLayerConfig layer, in PvLayerState state,
        PvCamState cam, int w, int h)
    {
        var image = ImageOf(snapshot.CharacterImage, w, h);
        var rect = PvStageCompositor.FitRect(image, layer.Cover, w, h);
        var tf = PvParallax.Compute(w, h, layer, state, cam,
            globalParallax: snapshot.GlobalParallax, antiBleed: snapshot.AntiBleed,
            parallaxOverride: snapshot.CameraAffectsCharacter ? PvParallax.FullFollow : null);

        var vts = snapshot.Vts;
        if (vts.HasHull && vts.HullMaxX > vts.HullMinX && vts.HullMaxY > vts.HullMinY)
            return Box(rect, tf.Matrix,
                (vts.HullMinX + vts.HullMaxX) / 2,
                (vts.HullMinY + vts.HullMaxY) / 2,
                (vts.HullMaxX - vts.HullMinX) / 2 * 1.03,
                (vts.HullMaxY - vts.HullMinY) / 2 * 1.03);

        if (image.Alpha is { Length: > 0 } alpha && image.AlphaWidth > 0 && image.AlphaHeight > 0)
        {
            if (AlphaBox(alpha, image.AlphaWidth, image.AlphaHeight) is { } box)
                return Box(rect, tf.Matrix, box.Cx, box.Cy, box.HalfX, box.HalfY);
        }

        return
        [
            Vector2.Transform(new Vector2(rect.X, rect.Y), tf.Matrix),
            Vector2.Transform(new Vector2(rect.X + rect.Width, rect.Y), tf.Matrix),
            Vector2.Transform(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), tf.Matrix),
            Vector2.Transform(new Vector2(rect.X, rect.Y + rect.Height), tf.Matrix),
        ];
    }

    private static Vector2[] Box(Rect rect, Matrix3x2 matrix, double cx, double cy, double halfX, double halfY)
    {
        var halfW = rect.Width / 2.0;
        var halfH = rect.Height / 2.0;
        var center = new Vector2(
            (float)(rect.X + rect.Width / 2 + cx * halfW),
            (float)(rect.Y + rect.Height / 2 - cy * halfH));
        var boxHalfX = (float)(halfX * halfW);
        var boxHalfY = (float)(halfY * halfH);

        Vector2 Corner(float sx, float sy) => Vector2.Transform(
            new Vector2(center.X + sx * boxHalfX, center.Y + sy * boxHalfY), matrix);

        return [Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1)];
    }

    private static (double Cx, double Cy, double HalfX, double HalfY)? AlphaBox(byte[] alpha, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (alpha[row + x] <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY) return null;

        var u0 = minX / (double)Math.Max(1, width - 1);
        var u1 = (maxX + 1) / (double)Math.Max(1, width - 1);
        var v0 = minY / (double)Math.Max(1, height - 1);
        var v1 = (maxY + 1) / (double)Math.Max(1, height - 1);
        return (u0 + u1 - 1, 1 - (v0 + v1), u1 - u0, v1 - v0);
    }

    private static Rect MapBounds(Matrix3x2 inv, IReadOnlyList<Vector2> points)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        for (var i = 0; i < points.Count; i++)
        {
            var p = Vector2.Transform(points[i], inv);
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        var x0 = Math.Max(a.X, b.X);
        var y0 = Math.Max(a.Y, b.Y);
        var x1 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x1 <= x0 || y1 <= y0 ? new Rect(0, 0, 0, 0) : new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static Rect Union(Rect a, Rect b)
    {
        var x0 = Math.Min(a.X, b.X);
        var y0 = Math.Min(a.Y, b.Y);
        var x1 = Math.Max(a.X + a.Width, b.X + b.Width);
        var y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X
        && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
}
