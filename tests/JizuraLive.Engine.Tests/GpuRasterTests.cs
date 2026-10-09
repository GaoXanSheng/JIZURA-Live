using System;
using System.IO;
using System.Threading;
using JizuraLive.App;
using JizuraLive.Engine;
using JizuraLive.Spout;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class GpuRasterTests
{
    [Fact]
    public void GpuRaster_InitAndFlush()
    {
        var host = GpuRasterHost.TryGet(out var err);
        if (host == null) return;

        host.Enter();
        try
        {
            var surf = host.Rent(1920, 1080);
            host.Activate(surf);
            surf.Skia.Canvas.Clear(SKColors.Red);
            host.FlushAndSync();
        }
        finally
        {
            host.Exit();
        }
    }

    [Fact]
    public void GpuRaster_MultiChannel_SeparateSurfaces()
    {
        var host = GpuRasterHost.TryGet(out var err);
        if (host == null) return;

        host.Enter();
        try
        {
            var mainSurf = host.Rent("MAIN", 1920, 1080);
            var lyricsSurf = host.Rent("LYRICS", 1920, 1080);
            Assert.NotSame(mainSurf, lyricsSurf);
            Assert.NotEqual(mainSurf.TexturePointer, lyricsSurf.TexturePointer);

            host.Activate(mainSurf);
            mainSurf.Skia.Canvas.Clear(SKColors.Red);
            host.FlushAndSync();

            host.Activate(lyricsSurf);
            lyricsSurf.Skia.Canvas.Clear(SKColors.Transparent);
            host.FlushAndSync();
        }
        finally
        {
            host.Exit();
        }
    }

    [Fact]
    public void GpuRaster_RenderManyFramesAndFlush()
    {
        EngineBoot.InstallPacks();
        var host = GpuRasterHost.TryGet(out var err);
        if (host == null) return;

        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った\n消えそうな灯火を握りしめて\n僕らはまだ歩いていく";
        var plan = Planner.Plan(project, null);

        host.Enter();
        try
        {
            using var renderer = new Renderer();
            var surf = host.Rent(1920, 1080);
            host.Activate(surf);

            for (int i = 0; i < 100; i++)
            {
                double t = i * 0.05;
                renderer.Frame(surf.Skia.Canvas, plan, t, new RenderOpts
                {
                    Scale = 1.0,
                    Transparent = false,
                    Stage = true,
                });
                host.FlushAndSync();
            }
        }
        finally
        {
            host.Exit();
        }
    }

    [Fact]
    public void SpoutOut_GpuPipeline_Smoke()
    {
        EngineBoot.InstallPacks();
        var host = GpuRasterHost.TryGet(out var err);
        if (host == null) return;

        var pe = new PreviewEngine();
        pe.SetProject(Project.NewDefault());
        using var spout = new SpoutOut();
        var started = spout.Start(pe, new SpoutOutOptions
        {
            SenderName = "JIZURA-TEST",
            Width = 1920,
            Height = 1080,
            Fps = 30,
            LyricsEnabled = true,
            LyricsFps = 30,
        });
        Assert.True(started);
        pe.Play();
        Thread.Sleep(1000);
        spout.Stop();
    }

    [Fact]
    public void GpuRaster_SafePipeline_StressTest()
    {
        EngineBoot.InstallPacks();
        var host = GpuRasterHost.TryGet(out var err);
        if (host == null) return;

        var projPath = @"E:\CSharp\VTB-JIZURA\tools\TEST-我把我唱给你听.jizuralive";
        if (!File.Exists(projPath)) return;
        var project = ProjectIO.Open(projPath);
        project.Aspect = "9:16"; // 竖屏，强制触发 letterbox

        var pe = new PreviewEngine();
        pe.SetProject(project);

        // 背景并发频繁 GC，验证无 0xC000001D 与 0xC0000005 崩溃
        bool running = true;
        var gcThread = new Thread(() =>
        {
            while (Volatile.Read(ref running))
            {
                GC.Collect();
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        gcThread.Start();

        try
        {
            using var spout = new SpoutOut();
            var started = spout.Start(pe, new SpoutOutOptions
            {
                SenderName = "JIZURA-TEST-STRESS",
                Width = 1920,
                Height = 1080,
                Fps = 60,
                LyricsEnabled = true,
                LyricsFps = 60,
            });
            Assert.True(started);
            pe.Play();

            // 跑 3 秒 (180 帧)，在并发 GC 下看是否稳定运行
            Thread.Sleep(3000);
            spout.Stop();
        }
        finally
        {
            Volatile.Write(ref running, false);
            gcThread.Join();
        }
    }
}
