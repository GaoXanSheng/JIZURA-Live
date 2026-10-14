using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JizuraLive.Engine;
using JizuraLive.Pv;
using SkiaSharp;

namespace JizuraLive.App.Views;

public partial class PvActorPreviewView : UserControl, IDisposable
{
    private const int ViewWidth = 480;
    private const int ViewHeight = 270;

    private WriteableBitmap? _wbmp;
    private SKBitmap? _dummyModel;
    private long _beatHitTriggerAt;
    private double _localTime;
    private long _lastRenderTs;
    private bool _isDisposed;

    public PvActorPreviewView()
    {
        InitializeComponent();

        _wbmp = new WriteableBitmap(ViewWidth, ViewHeight, 96, 96, PixelFormats.Bgra32, null);
        ImgViewport.Source = _wbmp;

        SlBpm.ValueChanged += (_, _) => TblBpmVal.Text = SlBpm.Value.ToString("F0");

        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnRenderTick;
        };
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRenderTick;
        };
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_isDisposed || !IsVisible || _wbmp == null) return;

        var now = Stopwatch.GetTimestamp();
        if (_lastRenderTs != 0)
        {
            var dt = (now - _lastRenderTs) / (double)Stopwatch.Frequency;
            _localTime += Math.Clamp(dt, 0.0, 0.1);
        }
        _lastRenderTs = now;

        var bpm = SlBpm.Value;
        var isBeatHit = (Environment.TickCount64 - _beatHitTriggerAt) < 220;

        // 尝试从真实 Spout 接收端中抓取当前源位图
        SKBitmap? sourceBmp = null;
        var main = App.MainInstance;
        if (main != null && main.Spout.IsRunning && main.Spout.LiveIns.Count > 0)
        {
            sourceBmp = main.Spout.LiveIns[0].FrontForRender;
        }

        TblEmptyHint.Visibility = sourceBmp != null ? Visibility.Collapsed : Visibility.Visible;
        var drawBmp = sourceBmp ?? GetOrCreateDummyModel();

        // 评估角色动态姿态（节拍律动、高光协同、轮廓光脉冲）
        var enableBounce = CkSimulateBounce.IsChecked == true;
        var pose = enableBounce
            ? PvCharacterActor.EvaluatePose(_localTime, bpm, energy: 0.75, isFlashActive: isBeatHit, isBeatHit: isBeatHit)
            : new CharacterActorPose(0, 1, 1, isBeatHit ? 0.6f : 0, 1);

        // 离屏渲染一帧
        _wbmp.Lock();
        try
        {
            var info = new SKImageInfo(ViewWidth, ViewHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, _wbmp.BackBuffer, _wbmp.BackBufferStride);
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(13, 13, 17));

            var cx = ViewWidth / 2f;
            var cy = ViewHeight / 2f;

            // 1. 若有重音打击，绘制冲击波
            if (isBeatHit)
            {
                var progress = Math.Clamp((Environment.TickCount64 - _beatHitTriggerAt) / 220.0, 0.0, 1.0);
                PvEffectEngine.DrawBeatShockwave(canvas, cx, cy + 30f, progress, 140f, SKColors.Magenta);
            }

            // 2. 绘制角色本体（带弹性下沉回弹、呼吸背光、环境微调色）
            var destRect = LiveLayer.ComputeDest(
                drawBmp.Width, drawBmp.Height, ViewWidth, ViewHeight,
                scale: 0.82, offX: 0, offY: 0.05, cover: false);

            var bottomY = destRect.Bottom;
            canvas.Save();
            try
            {
                // 围绕脚底做 Squash & Stretch 节拍形变
                if (enableBounce && Math.Abs(pose.BounceOffsetY) > 0.01f)
                {
                    canvas.Translate(cx, bottomY);
                    canvas.Translate(0, pose.BounceOffsetY);
                    canvas.Scale(pose.SquashScaleX, pose.StretchScaleY);
                    canvas.Translate(-cx, -bottomY);
                }

                // 背光脉冲泛光
                var glowIntensity = 0.25 * (pose.RimGlowPulse > 0 ? pose.RimGlowPulse : 1.0);
                PvColorGrading.DrawRimGlow(canvas, destRect, SKColors.Cyan, glowIntensity, 20.0);

                // 角色绘制
                var (_, actorPaint) = PvCharacterActor.BuildActorTransformAndPaint(
                    SKMatrix.Identity, cx, bottomY, pose, ambientFilter: null, baseAlpha: 1.0);

                using (actorPaint)
                {
                    var px = drawBmp.PeekPixels();
                    if (px != null)
                    {
                        using var img = SKImage.FromPixels(px.Info, px.GetPixels(), px.RowBytes);
                        if (img != null)
                        {
                            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                            canvas.DrawImage(img, destRect, sampling, actorPaint);
                        }
                    }
                }
            }
            finally
            {
                canvas.Restore();
            }

            // 3. 浮动氛围星尘
            PvEffectEngine.DrawFloatingDust(canvas, ViewWidth, ViewHeight, _localTime, density: 0.4, SKColors.Cyan);

            canvas.Flush();
            _wbmp.AddDirtyRect(new Int32Rect(0, 0, ViewWidth, ViewHeight));
        }
        finally
        {
            _wbmp.Unlock();
        }
    }

    private SKBitmap GetOrCreateDummyModel()
    {
        if (_dummyModel != null) return _dummyModel;
        var bmp = new SKBitmap(new SKImageInfo(180, 240, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);

        // 绘制一个精致的虚拟角色半身剪影（头、发、躯干）
        using var bodyPaint = new SKPaint
        {
            Color = new SKColor(80, 160, 240, 220),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        // 头部
        c.DrawCircle(90, 60, 38, bodyPaint);
        // 肩部与躯干
        using var torsoPath = new SKPath();
        torsoPath.MoveTo(45, 120);
        torsoPath.QuadTo(90, 95, 135, 120);
        torsoPath.LineTo(155, 230);
        torsoPath.LineTo(25, 230);
        torsoPath.Close();
        c.DrawPath(torsoPath, bodyPaint);

        _dummyModel = bmp;
        return _dummyModel;
    }

    private void BtnBeatTap_Click(object sender, RoutedEventArgs e)
    {
        _beatHitTriggerAt = Environment.TickCount64;
    }

    private void BtnPopOut_Click(object sender, RoutedEventArgs e)
    {
        PvActorWindow.OpenOrFocus();
    }

    public void Dispose()
    {
        _isDisposed = true;
        CompositionTarget.Rendering -= OnRenderTick;
        _dummyModel?.Dispose();
        _dummyModel = null;
        _wbmp = null;
    }
}
