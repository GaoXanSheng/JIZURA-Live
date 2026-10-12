using System.IO;
using JizuraLive.App;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using NAudio.Wave;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 云试听播放链与播放器模式会话。挂 EngineRegistry collection：会话测试要构造 PreviewEngine
/// （会往进程级注册表装包），与注册表顺序测试并行跑会把别人的顺序断言污染掉。
/// </summary>
/// <remarks>
/// 试听播放链（CloudPreview）的实机冒烟：本地正弦 WAV 跑一遍「建链 → 播放推进 → 拖进度 → 到头判定」。
/// 这条链有设备依赖（同 GpuRasterTests 对 GPU 的依赖），但值得留着：
/// 它抓住过一个致命问题——链上套了补零包装（挡 WDL 重采样在源 EOF 时越界）后，源放完设备不会自己停、
/// PlaybackStopped 也不会来，于是「放完了」永远检测不到，顺序播放会卡死在第一首。
/// </summary>
[Collection("EngineRegistry")]
public class CloudPreviewTests
{
    [Fact]
    public async Task 试听链_播放推进_拖进度_到头判定()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jz-prev-{Guid.NewGuid():N}.wav");
        using (var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 1))) // 44.1k 单声道 → 链上要重采样到 48k
        {
            for (var i = 0; i < 44100 * 1.2; i++)
                w.WriteSample((float)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 0.2));
        }

        var pv = new CloudPreview { Volume = 0 }; // 静音跑，只验链
        try
        {
            Assert.True(await pv.PlayAsync(path), "建链应成功");
            Assert.True(pv.HasTrack);
            Assert.Equal(1.2, pv.Duration, 1);
            Assert.True(pv.IsPlaying, "挂链后应在播");
            Assert.False(pv.AtEnd, "刚起播不该判成到头");

            await Task.Delay(500);
            Assert.True(pv.Position > 0.3, $"位置应推进，实际 {pv.Position:F2}");

            pv.Seek(1.0);
            await Task.Delay(400);
            Assert.True(pv.AtEnd, $"seek 到 1.0 再放 0.4s 应判为放完，实际位置 {pv.Position:F2}");

            pv.Teardown();
            Assert.False(pv.HasTrack);
            Assert.False(pv.AtEnd);
            // 时长与链必须同生共死：拆链后不许留下「时长还在 / 位置还在」的错位状态。
            // 播放器显示的「已播 x / 00:00」与播完不换歌（AtEnd 要求 dur>0）都源于这里被拆开。
            Assert.Equal(0, pv.Duration);
            Assert.Equal(0, pv.Position);

            // 换歌（换代后重挂链）：时长必须跟着新链恢复，不靠建链时写过的旧值
            Assert.True(await pv.PlayAsync(path), "换歌后重建链应成功");
            Assert.Equal(1.2, pv.Duration, 1);
        }
        finally
        {
            pv.Dispose();
            try { File.Delete(path); } catch { /* 设备句柄可能还没放完 */ }
        }
    }

    /// <summary>
    /// 连续换歌的哨兵：旧链拆除（waveOutReset/close）会让它的回调线程在 waveOutWrite 上抛
    /// InvalidParameter，这条异常属于已被替换的旧设备，绝不能被当成「当前设备故障」上报
    /// （上报会经 OnDeviceFailed 把刚挂上的新链一起拆掉：播放器表现为「一切歌就报音频设备异常，
    /// 试听已停止」，且时长清零、播完不换歌）。真机实测旧代码 33 次换歌撞出 20 次。
    ///
    /// 注意这条哨兵**抓不住**那个竞态：进程内（设备空闲、无 UI 抖动）即使停用守卫也复现不出来
    /// ——撞车窗口被真机的调度抖动（实测 300~480ms）放大才会高频触发。它守的是「连续换歌不出
    /// 设备故障」这个外部可见结果；竞态本身只能靠真机日志验证（修前 20/33，修后 0/N）。
    /// </summary>
    [Fact]
    public async Task 试听链_连续换歌_旧链拆除异常不上报()
    {
        var a = MakeTone("jz-sw-a");
        var b = MakeTone("jz-sw-b");
        var pv = new CloudPreview { Volume = 0 };
        var failures = 0;
        pv.Failed += () => Interlocked.Increment(ref failures);
        try
        {
            for (var i = 0; i < 8; i++)
            {
                Assert.True(await pv.PlayAsync(i % 2 == 0 ? a : b), $"第 {i + 1} 次换歌应建链成功");
                await Task.Delay(150); // 让设备真的在放：回调线程在跑，拆链才撞得上 waveOutWrite
            }
            await Task.Delay(400); // 等最后一次拆链的收尾回调落地
            Assert.Equal(0, failures);
        }
        finally
        {
            pv.Dispose();
            try { File.Delete(a); File.Delete(b); } catch { /* 设备句柄可能还没放完 */ }
        }
    }

    /// <summary>造一个 1.2 秒的正弦 WAV（44.1k 单声道 → 链上要重采样到 48k）。</summary>
    private static string MakeTone(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.wav");
        using var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        for (var i = 0; i < 44100 * 1.2; i++)
            w.WriteSample((float)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 0.2));
        return path;
    }

    /// <summary>播放器模式的进出：进模式快照工程、退出按快照还原（歌词/曲名/歌手是会话内唯一被改的字段）。</summary>
    [Fact]
    public void 播放器会话_进出模式还原工程与音频字段()
    {
        L.Initialize(null);
        EngineBoot.Install();
        using var preview = new PreviewEngine();
        var state = new EditorState(preview);
        state.Mutate(p =>
        {
            p.Title = "用户自己的歌";
            p.Artist = "用户";
            p.Lyrics = "[00:01.00]用户自己的词";
        }, "test", replan: false);

        using var svc = new CloudPlayService();
        var session = new CloudPlayerSession(state, svc);
        Assert.False(session.Active);
        session.Enter();
        Assert.True(session.Active);
        Assert.True(File.Exists(CloudPlayerSession.SessionFile), "进模式应留下会话快照（崩溃可还原）");

        // 会话内改歌（播放器模式的做法：不压历史，也不碰音频字段）
        state.MutateNoHistory(p =>
        {
            p.Title = "云歌";
            p.Lyrics = "[00:01.00]云歌的词";
        }, "test-song", replan: false);
        Assert.Equal("云歌", state.Project.Title);

        session.Exit();
        Assert.False(session.Active);
        Assert.Equal("用户自己的歌", state.Project.Title);
        Assert.Equal("用户", state.Project.Artist);
        Assert.Equal("[00:01.00]用户自己的词", state.Project.Lyrics);
        Assert.False(File.Exists(CloudPlayerSession.SessionFile), "正常退出应清掉会话文件");
    }

    /// <summary>上次播放器模式异常退出（会话文件还在）→ 启动时还原工程并删掉文件。</summary>
    [Fact]
    public void 播放器会话_遗留快照启动时还原()
    {
        L.Initialize(null);
        EngineBoot.Install();
        using var preview = new PreviewEngine();
        var state = new EditorState(preview);

        state.Mutate(p => p.Title = "上次会话里的云歌", "test", replan: false);
        AtomicFile.Write(CloudPlayerSession.SessionFile, state.Snapshot());
        state.Mutate(p => p.Title = "启动后加载的 autosave", "test", replan: false);

        using var svc = new CloudPlayService();
        var session = new CloudPlayerSession(state, svc);
        Assert.True(session.RecoverLeftoverIfAny());
        Assert.Equal("上次会话里的云歌", state.Project.Title);
        Assert.False(File.Exists(CloudPlayerSession.SessionFile));
        Assert.False(session.RecoverLeftoverIfAny()); // 没有遗留了就返回 false
    }
}
