using System.IO;
using System.Runtime.CompilerServices;
using JizuraLive.Render.Gpu;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 逐帧 golden 对拍的骨架：把一帧存成 PNG，与新渲染结果做「带容差」的像素比对。
/// 基于轻量级 ReadbackImage，彻底脱离外部绘制库。
/// </summary>
public static class GoldenImage
{
    /// <summary>golden 目录：放在测试源码旁的 golden/（而不是 bin 输出里），基准才可入库、可评审。</summary>
    public static string Dir => Path.Combine(SourceDir, "golden");

    private static readonly string SourceDir = ResolveSourceDir();

    private static string ResolveSourceDir([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;

    /// <summary>JIZURA_GOLDEN_UPDATE=1/true/on 时把当前帧写回 golden。</summary>
    public static bool UpdateRequested =>
        Environment.GetEnvironmentVariable("JIZURA_GOLDEN_UPDATE") is "1" or "true" or "on";

    public static string PathOf(string name) => Path.Combine(Dir, name + ".png");

    public static bool Exists(string name) => File.Exists(PathOf(name));

    public static void Save(string name, ReadbackImage image)
    {
        image.SavePng(PathOf(name));
    }

    public static ReadbackImage? TryLoad(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        try
        {
            return ReadbackImage.LoadPng(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>两图差异：尺寸是否一致、平均绝对差(0-255)、最大绝对差、超 8 灰阶像素占比。</summary>
    public readonly record struct Diff(bool SameSize, double MeanAbs, int MaxAbs, double PctOver8)
    {
        public override string ToString() =>
            SameSize
                ? $"mean={MeanAbs:F3}, max={MaxAbs}, over8={PctOver8 * 100:F3}%"
                : "尺寸不一致";
    }

    public static Diff Compare(ReadbackImage a, ReadbackImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return new Diff(false, double.MaxValue, 255, 1.0);

        var pa = a.BgraPixels;
        var pb = b.BgraPixels;
        if (pa.Length != pb.Length)
            return new Diff(false, double.MaxValue, 255, 1.0);

        long sum = 0;
        var max = 0;
        long over = 0;
        long n = pa.Length;

        for (var i = 0; i < n; i++)
        {
            var d = Math.Abs(pa[i] - pb[i]);
            sum += d;
            if (d > max) max = d;
            if (d > 8) over++;
        }

        return new Diff(true, n == 0 ? 0 : (double)sum / n, max, n == 0 ? 0 : (double)over / n);
    }

    /// <summary>逐字节完全相同（确定性闸用）。</summary>
    public static bool BitIdentical(ReadbackImage a, ReadbackImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        return a.BgraPixels.AsSpan().SequenceEqual(b.BgraPixels.AsSpan());
    }

    /// <summary>把一张图存进 golden（配合 UpdateRequested 用）。</summary>
    public static void SaveOrUpdate(string name, ReadbackImage bmp)
    {
        Save(name, bmp);
    }
}
