// 外部实时层（VTS Spout 输入）的两块契约：
//   1) 几何：contain/cover × 用户缩放 × 偏移 → 目标矩形（纯函数，逐值对拍）；
//   2) 合成：带实时层渲染的一帧必须与不带的那一帧不同，且差异像素就是层的颜色
//      ——这条同时钉住「画在背景之上、歌词之下」的接线没断。

using System;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class LiveLayerTests
{
    [Fact]
    public void ComputeDest_Contain_Keeps_Whole_Source()
    {
        // 源 1920×1080 进 1920×1080 画幅：1:1 铺满
        var d = LiveLayer.ComputeDest(1920, 1080, 1920, 1080, 1, 0, 0, cover: false);
        Assert.Equal(0, d.Left, 3);
        Assert.Equal(0, d.Top, 3);
        Assert.Equal(1920, d.Right, 3);
        Assert.Equal(1080, d.Bottom, 3);
    }

    [Fact]
    public void ComputeDest_Contain_Letterboxes_Tall_Source()
    {
        // 竖屏源（1080×1920）进横屏画幅：高度顶满、左右留边
        var d = LiveLayer.ComputeDest(1080, 1920, 1920, 1080, 1, 0, 0, cover: false);
        Assert.Equal(1080, d.Height, 3);
        Assert.Equal(607.5, d.Width, 3);
        Assert.Equal((1920 - 607.5) / 2, d.Left, 3);
        Assert.Equal(0, d.Top, 3);
    }

    [Fact]
    public void ComputeDest_Cover_Fills_And_Crops()
    {
        var d = LiveLayer.ComputeDest(1080, 1920, 1920, 1080, 1, 0, 0, cover: true);
        Assert.Equal(1920, d.Width, 3);
        Assert.Equal(3413.333, d.Height, 3);
        Assert.True(d.Top < 0, "cover 应当上下裁切（顶边为负）");
    }

    [Fact]
    public void ComputeDest_Scale_And_Offset_Are_Relative_To_Canvas()
    {
        var d = LiveLayer.ComputeDest(100, 100, 1000, 500, 0.5, 0.25, -0.2, cover: false);
        // contain 基准 = min(10, 5) = 5 → 乘 0.5 → 2.5 → 250×250，居中 (375,125)，再偏移
        Assert.Equal(250, d.Width, 3);
        Assert.Equal(250, d.Height, 3);
        Assert.Equal(375 + 250, d.Left, 3);   // +0.25*1000
        Assert.Equal(125 - 100, d.Top, 3);    // -0.2*500
    }

    [Fact]
    public void ComputeDest_Degenerate_Inputs_Are_Empty()
    {
        Assert.True(LiveLayer.ComputeDest(0, 100, 100, 100, 1, 0, 0, false).IsEmpty);
        Assert.True(LiveLayer.ComputeDest(100, 100, 0, 100, 1, 0, 0, false).IsEmpty);
    }

    [Fact]
    public void From_Clamps_Out_Of_Range_Options()
    {
        var o = new LiveLayerOptions { Alpha = 5, Scale = 0, Blur = -3, Glow = 9, OffsetX = 0.3 };
        using var bmp = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        var l = LiveLayer.From(o, bmp);
        Assert.Equal(1, l.Alpha, 6);
        Assert.Equal(0.05, l.Scale, 6);
        Assert.Equal(0, l.Blur, 6);
        Assert.Equal(1, l.Glow, 6);
        Assert.Equal(0.3, l.OffsetX, 6);
    }

    [Fact]
    public void HueFilter_Zero_Is_Null()
    {
        Assert.Null(LiveLayer.HueFilter(0));
        Assert.Null(LiveLayer.HueFilter(0.2));
        using var f = LiveLayer.HueFilter(90);
        Assert.NotNull(f);
    }

    [Fact]
    public void From_Carries_Rotation()
    {
        var o = new LiveLayerOptions { Rotation = 37.5 };
        using var bmp = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        Assert.Equal(37.5, LiveLayer.From(o, bmp).Rotation, 6);
    }

    [Fact]
    public void Pv_Sources_RoundTrip_Json()
    {
        var s = new App.Settings();
        s.Pv.Enabled = true;
        s.Pv.FollowCamera = false;
        s.Pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = "CustomVts", Alpha = 0.85, Rotation = 12 });
        s.Pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = "GameCapture", Scale = 1.25, Cover = true });
        s.Pv.Revision = 99; // JsonIgnore

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(s);
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<App.Settings>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.Pv.Enabled);
        Assert.False(restored.Pv.FollowCamera);
        Assert.Equal(2, restored.Pv.Sources.Count);
        Assert.Equal("CustomVts", restored.Pv.Sources[0].SenderName);
        Assert.Equal(0.85, restored.Pv.Sources[0].Alpha, 3);
        Assert.Equal(12, restored.Pv.Sources[0].Rotation, 3);
        Assert.Equal("GameCapture", restored.Pv.Sources[1].SenderName);
        Assert.Equal(1.25, restored.Pv.Sources[1].Scale, 3);
        Assert.True(restored.Pv.Sources[1].Cover);
        Assert.Equal(0, restored.Pv.Revision); // Revision 不进 settings.json
    }
}
