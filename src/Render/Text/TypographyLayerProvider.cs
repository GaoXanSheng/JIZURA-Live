using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

/// <summary>
/// 纯 Direct2D / DirectWrite 打造的现代实时 PV 歌词排版层。
/// 支持毫秒级时间轴匹配、字级动态入场与高级感阴影发光。
/// </summary>
public sealed class TypographyLayerProvider : IPvLayerProvider, IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private OffscreenTarget? _target;
    private IReadOnlyList<LyricClip> _clips = Array.Empty<LyricClip>();

    /// <summary>同刻合并后的多行文本按行拆开画：DwFont 的文本格式是单行不换行的，'\n' 得自己分层。</summary>
    private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];

    private int _w, _h;
    private double _scale = 1.0;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int Width => _w;
    public int Height => _h;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public TypographyLayerProvider(string id, D3DDevice gpu, D2DHost host,
        int designWidth = 1920, int designHeight = 1080)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
    }

    /// <summary>
    /// 旧入口（PvPipeline.SetLyrics 转发 KrcLine）：转成 Clip 后走同一条查找路径，
    /// 不再让动效歌词自己算一套「当前句」。
    /// </summary>
    public void SetLyrics(IReadOnlyList<KrcLine>? lines) =>
        SetClips(lines is null ? Array.Empty<LyricClip>() : LrcParser.ToClips(lines));

    /// <summary>直接接收已冻结的 Clip 序列（歌词轨编辑后的产物）。要求按 StartMs 升序。</summary>
    public void SetClips(IReadOnlyList<LyricClip>? clips)
    {
        _clips = clips ?? Array.Empty<LyricClip>();
        _version++;
    }

    public void SetScale(double scale)
    {
        scale = Math.Clamp(scale, 0.05, 1.0);
        if (Math.Abs(scale - _scale) < 1e-4) return;
        _scale = scale;
        _version++;
    }

    public void Render(double time)
    {
        var w = Math.Max(16, (int)Math.Round(DesignWidth * _scale));
        var h = Math.Max(16, (int)Math.Round(DesignHeight * _scale));

        if (_target is null || w != _w || h != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _host.Ctx, w, h, sampleable: true);
            _w = w;
            _h = h;
            _version++;
        }

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        if (_clips.Count > 0)
        {
            // 唯一真源：命中不了样本（空档 / t == EndMs / 越界）就什么都不画，等清屏
            var sample = LrcParser.FindSample(_clips, time);
            if (sample is { } cur)
            {
                var progress = (float)cur.ProgressAt(time);

                // 进出场曲线计算
                float alpha = 1f;
                float offsetY = 0f;
                if (progress < 0.15f)
                {
                    // 进场前 15%：上浮 + 渐现
                    var inP = progress / 0.15f;
                    alpha = inP;
                    offsetY = (1f - inP) * 16f * (float)_scale;
                }
                else if (progress > 0.85f)
                {
                    // 退场后 15%：微沉 + 渐隐
                    var outP = (progress - 0.85f) / 0.15f;
                    alpha = 1f - outP;
                    offsetY = -outP * 10f * (float)_scale;
                }

                var font = d.Fonts.Get("Microsoft YaHei UI", 52f * (float)_scale, weight: Vortice.DirectWrite.FontWeight.Bold);
                var cx = w / 2f;
                var cy = h * 0.75f + offsetY;

                // 同刻合并的文本是多行的：逐行各自排版，整块以 cy 为中心
                // （单行时与旧路径完全一致，多行不会被裁掉半截）
                var blockLines = cur.Text.Split(LineSeparators, StringSplitOptions.None);
                var lineHeight = font.LineHeight;
                var firstBaseline = cy - (blockLines.Length - 1) * lineHeight / 2f;

                for (var i = 0; i < blockLines.Length; i++)
                {
                    var line = blockLines[i];
                    if (line.Length == 0) continue; // 合并文本里的空行只占排版高度，没有字形

                    var baseline = firstBaseline + i * lineHeight;

                    // 1) 柔和外发光底影
                    d.DrawText(line, cx, baseline + 2f, TextAlign2D.Center, font, new Paint2D
                    {
                        Brush = new SolidBrush2D(new Color4(0f, 0.05f, 0.15f, alpha * 0.6f)),
                    });

                    // 2) 主高亮正文
                    d.DrawText(line, cx, baseline, TextAlign2D.Center, font, new Paint2D
                    {
                        Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, alpha)),
                    });
                }
            }
        }

        d.End();
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    /// <summary>
    /// 回读本层离屏面的像素（**仅测试/诊断用**：会阻塞到 GPU 完成，逐帧调用会拖垮帧率）。
    /// 空档清屏这类断言必须看像素——只查 HasContent 只说明「有目标面」，是假绿。
    /// </summary>
    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    public void Dispose()
    {
        _target?.Dispose();
    }
}
