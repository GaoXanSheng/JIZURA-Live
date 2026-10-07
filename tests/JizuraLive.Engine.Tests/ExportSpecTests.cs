using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

// ExportSpec（frontend/src/jizura/export-spec.ts 的移植）勾选模型对拍
[Collection("EngineRegistry")]
public class ExportSpecTests
{
    [Fact]
    public void NormalizeCfg_CleansDirtyInput_AndDefaults()
    {
        // 非对象/空 → 默认勾选
        var d = ExportSpec.NormalizeCfg(null);
        Assert.Equal(ExportCfg.Default, d);

        // 非法 format/bgStyle 归 mp4/off，非法布尔回默认值（对齐 TS 的 typeof v === 'boolean'）
        var c = ExportSpec.NormalizeCfg(new JObject
        {
            ["format"] = "avi",
            ["bgStyle"] = "blue",
            ["visuals"] = "yes",
            ["subs"] = true,
            ["bg"] = false,
        });
        Assert.Equal("mp4", c.Format);
        Assert.Equal("off", c.BgStyle);
        Assert.True(c.Visuals);   // "yes" 非布尔 → 默认 true
        Assert.True(c.Subs);
        Assert.False(c.Bg);
    }

    [Fact]
    public void CfgJsonRoundTrip_MatchesTsContract()
    {
        var cfg = new ExportCfg("webm", Visuals: true, Subs: true, Bg: false,
            BgStyle: "green", Audio: true, Layered: false, ToFile: true);
        var o = ExportSpec.ToJObject(cfg);
        // 键名与 TS 的 ExportCfg interface 完全一致（project.exportCfg 落盘契约）
        Assert.Equal(
            """{"format":"webm","visuals":true,"subs":true,"bg":false,"bgStyle":"green","audio":true,"layered":false,"toFile":true}""",
            o.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(cfg, ExportSpec.NormalizeCfg(o));
    }

    [Fact]
    public void BuildSpec_WebmTransparentFrontLayer()
    {
        // 不勾背景 = 透明 alpha + 只画前景层（无 HUD），字幕只在勾渲染画面时烤入
        var r = ExportSpec.BuildSpec(
            ExportSpec.NormalizeCfg(new JObject { ["format"] = "webm", ["bg"] = false, ["subs"] = true }),
            new SpecCtx(SubOn: true, AudioLoaded: true));
        Assert.True(r.Ok);
        Assert.Equal("webm", r.Spec!.Kind);
        Assert.True(r.Spec.Transparent);
        Assert.Equal("front", r.Spec.Layer);
        Assert.True(r.Spec.NoHud);
        Assert.False(r.Spec.CustomDrawOnly);
        Assert.True(r.Spec.BakeSubs);
        Assert.True(r.WavToo); // 音频勾选默认开 → WebM 无法内嵌，另存 WAV
    }

    [Fact]
    public void BuildSpec_Mp4RejectsTransparent_AndPicksFileKind()
    {
        // MP4 无背景 → 运行时校验失败 + 中文提示
        var bad = ExportSpec.BuildSpec(
            ExportSpec.NormalizeCfg(new JObject { ["format"] = "mp4", ["bg"] = false }),
            new SpecCtx(SubOn: true, AudioLoaded: true));
        Assert.False(bad.Ok);
        Assert.Contains("MP4", bad.Error);

        // toFile → mp4file kind；绿幕形态折进 keyBg
        var ok = ExportSpec.BuildSpec(
            ExportSpec.NormalizeCfg(new JObject { ["format"] = "mp4", ["toFile"] = true, ["bgStyle"] = "green" }),
            new SpecCtx(SubOn: true, AudioLoaded: true));
        Assert.True(ok.Ok);
        Assert.Equal("mp4file", ok.Spec!.Kind);
        Assert.True(ok.Spec.EmbedAudio);
        Assert.Equal("green", ok.Spec.KeyBg);
        Assert.False(ok.WavToo);
    }

    [Fact]
    public void LegacyKinds_MapToEquivalentSpecs()
    {
        var p = new Project { KeyBg = "green" };
        var mp4 = ExportSpec.SpecFromLegacyKind("mp4", p)!;
        Assert.Equal("mp4", mp4.Kind);
        Assert.True(mp4.BakeSubs);   // 旧版只有 mp4 家族烤字幕
        Assert.True(mp4.EmbedAudio);
        Assert.Equal("green", mp4.KeyBg);

        var pngl = ExportSpec.SpecFromLegacyKind("pngl", p)!;  // 分层透明 PNG
        Assert.Equal("png", pngl.Kind);
        Assert.True(pngl.Transparent);
        Assert.True(pngl.Layers);
        Assert.False(pngl.BakeSubs);

        Assert.Null(ExportSpec.SpecFromLegacyKind("mov", p));
    }
}
