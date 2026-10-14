// PV 模式设置模型回归：
//   · 源列表的渲染侧快照（关着时空表；并发增删留在列表里的 null 槽被跳过，不喂给渲染线程）；
//   · 旧配置迁移：历史 settings.json 里的单路 Live 块搬进 Pv.Sources，之后不再写回（走真实 Load/Save 路径，
//     顺带把 JSON 键名钉在测试里）。

using System;
using System.IO;
using JizuraLive.App;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class PvOptionsTests
{
    [Fact]
    public void Snapshot_Respects_Enabled_And_Skips_Nulls()
    {
        var pv = new PvOptions();
        pv.Sources.Add(new LiveLayerOptions { SenderName = "A" });
        Assert.Empty(pv.Snapshot()); // 关着：空表（调用方不用自己判 Enabled）

        pv.Enabled = true;
        Assert.Single(pv.Snapshot());

        // 模拟 UI 线程增删列表时恰被渲染线程读到：某个槽还是 null 或者已被清掉
        pv.Sources.Add(null!);
        pv.Sources.Add(new LiveLayerOptions { SenderName = "B" });
        var snap = pv.Snapshot();
        Assert.Equal(2, snap.Length);
        Assert.Equal("A", snap[0].SenderName);
        Assert.Equal("B", snap[1].SenderName);
    }

    [Fact]
    public void Legacy_Live_Block_Migrates_Into_Pv_And_Is_Not_Written_Back()
    {
        var file = Path.Combine(AppDirs.ConfigDir, "settings.json");
        var backup = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            Directory.CreateDirectory(AppDirs.ConfigDir);
            // 老版本的配置：单路外部输入（VTS），PV 键还不存在
            File.WriteAllText(file, """
            {
              "SpoutName": "JIZURA-live",
              "Live": {
                "Enabled": true,
                "SenderName": "VTubeStudio",
                "Alpha": 0.7,
                "Scale": 1.4,
                "OffsetX": 0.1,
                "Rotation": 0
              }
            }
            """);

            var s = Settings.Load();

            Assert.True(s.Pv.Enabled);     // 老配置开着外部输入 → 合成总开关跟着搬（升级不该悄悄停掉合成）
            var src = Assert.Single(s.Pv.Sources);
            Assert.True(src.Enabled);
            Assert.Equal("VTubeStudio", src.SenderName);
            Assert.Equal(0.7, src.Alpha, 3);
            Assert.Equal(1.4, src.Scale, 3);
            Assert.Equal(0.1, src.OffsetX, 3);
            Assert.True(s.Pv.FollowCamera); // 迁移不带这个新键：取默认（跟随镜头）
            Assert.Null(s.Live);            // 已迁移，不再持有旧块

            s.Save();
            var saved = JObject.Parse(File.ReadAllText(file));
            Assert.Null(saved["Live"]);                    // 不再写回旧键
            Assert.NotNull(saved["Pv"]);                   // 新配置落在 Pv 下
            Assert.Single(saved["Pv"]!["Sources"]!);       // 源进了列表
        }
        finally
        {
            if (backup != null) File.WriteAllText(file, backup);
            else if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Legacy_Disabled_Live_Does_Not_Enable_Pv()
    {
        // 反向：老配置里外部输入是关着的 → 迁移不能把合成打开（否则用户升级后画面凭空多一路源）
        var file = Path.Combine(AppDirs.ConfigDir, "settings.json");
        var backup = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            Directory.CreateDirectory(AppDirs.ConfigDir);
            File.WriteAllText(file, """{ "Live": { "Enabled": false, "SenderName": "VTubeStudio" } }""");

            var s = Settings.Load();

            Assert.False(s.Pv.Enabled);
            Assert.Single(s.Pv.Sources);   // 参数仍保留（用户可能只是临时关掉）
            Assert.False(s.Pv.Sources[0].Enabled);
        }
        finally
        {
            if (backup != null) File.WriteAllText(file, backup);
            else if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Fresh_Settings_Keeps_Pv_Empty()
    {
        // 没有 Live 键的新配置不该被迁移出「幽灵源」
        var s = new Settings();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(s);
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Settings>(json);
        Assert.NotNull(restored);
        Assert.Empty(restored!.Pv.Sources);
        Assert.Null(restored.Live);
    }
}
