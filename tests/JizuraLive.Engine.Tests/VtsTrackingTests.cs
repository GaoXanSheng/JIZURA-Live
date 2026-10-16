using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Vts;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// VTS 追踪联动的回归闸：响应解析要能容忍缺字段，映射规则（死区/眨眼边沿/手位）要可预期，
/// 驱动线程要能在数据停更时归位。
/// </summary>
public class VtsTrackingTests
{
    private readonly ITestOutputHelper _out;

    public VtsTrackingTests(ITestOutputHelper output) => _out = output;

    // ---------------- 解析 ----------------

    [Fact]
    public void ParsesInputParameterListPayload()
    {
        // 形状 = VTS 的 InputParameterListRequest 响应（defaultParameters/customParameters，每项 name+value）。
        // 之前这个用例按臆想的 TrackingDataRequest 形状写（position/rotation/handLeft），
        // 于是它和解析器互相印证、一起错的——真实 API 里根本没有那个消息。
        var json = JObject.Parse("""
        {
          "defaultParameters": [
            { "name": "FaceAngleX", "value": 9.0, "min": -30, "max": 30 },
            { "name": "FaceAngleY", "value": -15.0, "min": -30, "max": 30 },
            { "name": "FaceAngleZ", "value": 8.0, "min": -30, "max": 30 },
            { "name": "EyeOpenLeft", "value": 0.8, "min": 0, "max": 1 },
            { "name": "EyeOpenRight", "value": 0.6, "min": 0, "max": 1 },
            { "name": "MouthOpen", "value": 0.42, "min": 0, "max": 1 },
            { "name": "MouthSmile", "value": 0.5637, "min": 0, "max": 1 },
            { "name": "MocopiBodyAngleX", "value": 3.5 },
            { "name": "MocopiBodyAngleZ", "value": -1.5 },
            { "name": "HandLeftFound", "value": 1 },
            { "name": "HandRightFound", "value": 0 },
            { "name": "HandLeftPositionX", "value": -0.2 },
            { "name": "HandLeftPositionY", "value": 0.4 }
          ],
          "customParameters": [
            { "name": "MyParam", "value": 1.0 }
          ]
        }
        """);

        var s = VtsApi.ParseTracking(json);
        _out.WriteLine($"[追踪] 头 X{s.HeadX:F3} Y{s.HeadY:F3}，口 {s.MouthOpen:F2}，"
                       + $"笑 {s.MouthSmile:F2}，眼 {s.EyeOpenLeft:F2}/{s.EyeOpenRight:F2}");

        Assert.True(s.FaceFound);
        // 角度 → 归一化（映射侧的 HeadRange 按归一化单位写）
        Assert.Equal(0.30, s.HeadX, 4);
        Assert.Equal(-0.50, s.HeadY, 4);
        // 原始角度另存一份（度数）
        Assert.Equal(9.0, s.HeadAngleX, 4);
        Assert.Equal(-15.0, s.HeadAngleY, 4);
        Assert.Equal(8.0, s.HeadAngleZ, 4);
        Assert.Equal(3.5, s.BodyAngleX, 4);
        Assert.Equal(-1.5, s.BodyAngleZ, 4);
        Assert.Equal(0.8, s.EyeOpenLeft, 4);
        Assert.Equal(0.6, s.EyeOpenRight, 4);
        Assert.Equal(0.42, s.MouthOpen, 4);
        // VTS 的 MouthSmile 本来就是 0..1，直接透传（不再做 -1..1 的换算）
        Assert.Equal(0.5637, s.MouthSmile, 4);
        // 手部：VTS 1.35 **确实**导出了手 —— 旧文档里写的「拿不到手」是错的
        Assert.True(s.HandLeftFound);
        Assert.False(s.HandRightFound);
        Assert.Equal(-0.2, s.HandLeftX, 4);
        Assert.True(s.AtMs > 0);
    }

    [Fact]
    public void TrackingValuesWithoutKnownParameterNamesYieldNoFace()
    {
        // 换了个模型、参数名不是标准 Live2D 命名：不该崩，也不该假装有人脸
        var s = VtsApi.ParseTracking(JObject.Parse("""
        { "defaultParameters": [ { "name": "Custom1", "value": 1 } ] }
        """));
        Assert.False(s.FaceFound);
        Assert.Equal(0, s.MouthOpen);
    }

    [Fact]
    public void MissingFieldsFallBackToDefaults()
    {
        // 空响应 / 没有参数列表 → 空快照（AtMs 有值但 FaceFound=false，映射侧会走 Idle）
        var s = VtsApi.ParseTracking(JObject.Parse("""{ "defaultParameters": [] }"""));
        Assert.False(s.FaceFound);
        Assert.Equal(0, s.HeadX);
        Assert.Equal(0, s.MouthOpen);
        Assert.Equal(0, s.EyeOpenLeft);

        // 拿不到的三项必须是「明确的不存在」，不能是「碰巧为 0」
        Assert.Equal(0, s.HeadZ);
        Assert.False(s.HandLeftFound);
        Assert.False(s.HandRightFound);

        Assert.Equal(VtsTrackingState.Empty.FaceFound, VtsApi.ParseTracking(null).FaceFound);
    }

    // ---------------- 映射 ----------------

    private static VtsTrackingState Face(double x = 0, double y = 0, double z = 0,
        double mouth = 0, double eye = 1, double handX = 0, double handY = 0, bool hand = false,
        double tilt = 0) =>
        new(true, x, y, z, 0, 0, tilt, 0, 0, eye, eye, mouth, 0, hand, hand, handX, handY, handX, handY,
            Environment.TickCount64);

    [Fact]
    public void NoFaceYieldsIdle()
    {
        var d = VtsTrackingMap.Compute(VtsTrackingState.Empty, new VtsTrackingOptions(), false);
        Assert.False(d.Active);
        Assert.Equal(1.0, d.Zoom, 4);
    }

    [Fact]
    public void DeadZoneSuppressesMicroJitter()
    {
        var opt = new VtsTrackingOptions { HeadRange = 0.35, CameraGain = 1.0 };
        // 死区 6% 内（0.35*0.06 = 0.021）应当完全不动
        Assert.Equal(0, VtsTrackingMap.Compute(Face(x: 0.01), opt, false).CameraX, 4);
        // 明显位移要按比例给
        var far = VtsTrackingMap.Compute(Face(x: 0.35), opt, false);
        Assert.Equal(1.0, far.CameraX, 3);
    }

    [Fact]
    public void CameraSignsMatchScreenConvention()
    {
        var opt = new VtsTrackingOptions { HeadRange = 0.35, CameraGain = 1.0 };
        // 头往右 → 镜头往右（正）；头上抬（VTS y 向上为正）→ 镜头向上，屏幕坐标取负
        Assert.True(VtsTrackingMap.Compute(Face(x: 0.3), opt, false).CameraX > 0);
        Assert.True(VtsTrackingMap.Compute(Face(y: 0.3), opt, false).CameraY < 0);
    }

    [Fact]
    public void HeadDepthDrivesZoom()
    {
        var opt = new VtsTrackingOptions { HeadRange = 0.35, ZoomGain = 0.25 };
        Assert.True(VtsTrackingMap.Compute(Face(z: 0.35), opt, false).Zoom > 1.0);
        Assert.True(VtsTrackingMap.Compute(Face(z: -0.35), opt, false).Zoom < 1.0);
    }

    /// <summary>
    /// 头倾（FaceAngleZ，度）驱动镜头滚转：满量程 = TiltDegrees，方向同号，
    /// 死区与位移两路同一口径，TiltDegrees=0 就是完全不跟随。
    /// </summary>
    [Fact]
    public void HeadTiltDrivesCameraRoll()
    {
        var opt = new VtsTrackingOptions { TiltDegrees = 10.0 };
        // 标称满量程 ±30° → 满滚转；半量程按「死区外重新归一化」给（与位移同口径：(0.5-0.06)/0.94）
        Assert.Equal(10.0, VtsTrackingMap.Compute(Face(tilt: 30), opt, false).Rotation, 3);
        Assert.Equal(4.68, VtsTrackingMap.Compute(Face(tilt: 15), opt, false).Rotation, 2);
        Assert.Equal(-4.68, VtsTrackingMap.Compute(Face(tilt: -15), opt, false).Rotation, 2);
        // 死区（6% × 30° = 1.8°）内不动镜头
        Assert.Equal(0, VtsTrackingMap.Compute(Face(tilt: 1.5), opt, false).Rotation, 4);
        // 关掉 = 镜头不跟着倾
        Assert.Equal(0,
            VtsTrackingMap.Compute(Face(tilt: 30), new VtsTrackingOptions { TiltDegrees = 0 }, false).Rotation, 4);
    }

    /// <summary>
    /// 探针判龄与映射的失效口径：新鲜快照给得出姿态（含滚转），超过 <see cref="VtsTrackingProbe.StaleMs"/>
    /// 的快照被拒（调用方只能回基础取景）；没人脸时映射给静息量，不留上一帧的姿态偏移。
    /// </summary>
    [Fact]
    public void StaleSnapshotIsRejectedAndNoFaceYieldsNoPose()
    {
        var probe = new VtsTrackingProbe();
        var pose = Face(x: 0.35, tilt: 30);

        probe.Set(pose);
        Assert.True(probe.TryRead(VtsTrackingProbe.StaleMs, out var read));
        var fresh = VtsTrackingMap.Compute(read, new VtsTrackingOptions(), false);
        Assert.True(fresh.Active);
        Assert.True(fresh.CameraX > 0.3);
        Assert.NotEqual(0.0, fresh.Rotation);

        // 停更超过上限：判失效，镜头只能回 CameraConfig 基础值（没有「沿用上一帧」这条路）
        probe.Set(pose with { AtMs = Environment.TickCount64 - VtsTrackingProbe.StaleMs - 1 });
        Assert.False(probe.TryRead(VtsTrackingProbe.StaleMs, out _));

        // 丢脸：映射侧给静息量（Active=false、零偏移、Zoom=1），不残留任何姿态
        var lost = VtsTrackingMap.Compute(pose with { FaceFound = false }, new VtsTrackingOptions(), false);
        Assert.False(lost.Active);
        Assert.Equal(0, lost.CameraX, 4);
        Assert.Equal(0, lost.CameraY, 4);
        Assert.Equal(0, lost.Rotation, 4);
        Assert.Equal(1.0, lost.Zoom, 4);
    }

    [Fact]
    public void MouthDrivesEnergy()
    {
        var opt = new VtsTrackingOptions { MouthGain = 1.0 };
        Assert.Equal(0.7, VtsTrackingMap.Compute(Face(mouth: 0.7), opt, false).MouthEnergy, 4);
        // 增益可放大，但仍夹在 0..1
        var loud = new VtsTrackingOptions { MouthGain = 2.0 };
        Assert.Equal(1.0, VtsTrackingMap.Compute(Face(mouth: 0.7), loud, false).MouthEnergy, 4);
    }

    [Fact]
    public void BlinkFiresFlashOnlyOnTheEdge()
    {
        var opt = new VtsTrackingOptions { BlinkThreshold = 0.25, BlinkFlash = 0.8 };

        // 睁 → 闭：触发一次
        var closing = VtsTrackingMap.Compute(Face(eye: 0.1), opt, previousBlink: false);
        Assert.True(closing.Blink);
        Assert.Equal(0.8, closing.Flash, 4);

        // 持续闭眼：不再重复闪
        var held = VtsTrackingMap.Compute(Face(eye: 0.1), opt, previousBlink: true);
        Assert.True(held.Blink);
        Assert.Equal(0.0, held.Flash, 4);

        // 睁开：无闪光
        var opening = VtsTrackingMap.Compute(Face(eye: 0.9), opt, previousBlink: true);
        Assert.False(opening.Blink);
        Assert.Equal(0.0, opening.Flash, 4);
    }

    [Fact]
    public void HandPositionIsNormalizedAndClamped()
    {
        var opt = new VtsTrackingOptions { HandGain = 1.0 };
        var d = VtsTrackingMap.Compute(Face(hand: true, handX: 1.8, handY: -0.4), opt, false);
        Assert.Equal(1.0, d.HandX, 4);   // 超范围夹住
        Assert.Equal(-0.4, d.HandY, 4);

        var none = VtsTrackingMap.Compute(Face(hand: false), opt, false);
        Assert.Equal(0, none.HandX);
    }

    // ---------------- 驱动线程 ----------------

    private sealed class RecordingTarget : IVtsEffectTarget
    {
        public readonly List<VtsEffectDrive> Received = new();
        public void Apply(in VtsEffectDrive drive)
        {
            lock (Received) Received.Add(drive);
        }

        public VtsEffectDrive LastOrDefault()
        {
            lock (Received) return Received.Count > 0 ? Received[^1] : default;
        }
    }

    [Fact]
    public void DriverAppliesTrackingAndReturnsToIdleWhenDataStops()
    {
        var probe = new VtsTrackingProbe();
        var target = new RecordingTarget();
        using var driver = new VtsTrackingDriver(probe, target,
            new VtsTrackingOptions { HeadRange = 0.35, CameraGain = 1.0, FreshMs = 200 });
        driver.Start();

        probe.Set(Face(x: 0.35));
        var deadline = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < deadline && !target.LastOrDefault().Active)
            Thread.Sleep(10);

        var active = target.LastOrDefault();
        Assert.True(active.Active, "驱动线程没有把追踪数据推出去");
        Assert.True(active.CameraX > 0.9, $"CameraX 应为 1 附近，实际 {active.CameraX}");

        // 数据停更超过 FreshMs → 驱动应回到静息（否则镜头永远偏着）
        Thread.Sleep(400);
        deadline = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < deadline && target.LastOrDefault().Active)
            Thread.Sleep(10);

        Assert.False(target.LastOrDefault().Active, "追踪停更后没有归位");
        _out.WriteLine($"[vts] 驱动跑了 {driver.Ticks} 拍，收到 {target.Received.Count} 次 Apply");
    }

    // ---------------- 镜头链路（真管线） ----------------

    /// <summary>
    /// 可控假姿态快照 → 探针 → <see cref="PvPipeline.Prepare"/> → 舞台镜头：
    /// ① 新鲜姿态叠在 CameraConfig 基础取景之上（位移/缩放/滚转都得动，滚转不能再是恒 0）；
    /// ② 快照过期（&gt; <see cref="VtsTrackingProbe.StaleMs"/>）：逐分量回基础取景（rotation 回 7 而不是 0），能量归零。
    /// </summary>
    [GpuFact]
    public void TrackingPoseDrivesStageCameraAndStalePoseFallsBackToBase()
    {
        var gpu = D3DDevice.TryCreate(out var error);
        if (gpu is null) GpuRequire.Fail(error);

        using (gpu)
        {
            var scene = PvScene.CreateDefault();
            scene.Width = 1920;
            scene.Height = 1080;
            scene.Camera = new PvCameraConfig { X = -40, Y = 25, Scale = 1.2, Rotation = 7 };

            var probe = new VtsTrackingProbe();
            using var pipe = new PvPipeline(gpu, scene, new EffectGraph(), null, probe);

            var pose = VtsTrackingState.Empty with
            {
                FaceFound = true,
                HeadX = 0.8,
                HeadY = -0.5,
                HeadAngleZ = 30,
                MouthOpen = 0.9,
                EyeOpenLeft = 1,
                EyeOpenRight = 1,
                AtMs = Environment.TickCount64,
            };
            probe.Set(pose);
            pipe.Prepare(0.0);

            var drive = VtsTrackingMap.Compute(pose, new VtsTrackingOptions(), false);
            Assert.True(Math.Abs(drive.Rotation) > 0.5, "头倾应当产生可见的镜头滚转");
            var cam = pipe.Renderer.Stage.Camera;
            Assert.Equal(-40 + drive.CameraX * scene.Width * 0.10, cam.X, 3);
            Assert.Equal(25 + drive.CameraY * scene.Height * 0.10, cam.Y, 3);
            Assert.Equal(1.2, cam.Scale, 3);
            Assert.Equal(7 + drive.Rotation, cam.Rotation, 3);
            Assert.Equal(0.9, pipe.Renderer.Stage.Energy, 3);

            probe.Set(pose with { AtMs = Environment.TickCount64 - VtsTrackingProbe.StaleMs - 1 });
            pipe.Prepare(1.0 / 60);

            var stale = pipe.Renderer.Stage.Camera;
            Assert.Equal(-40, stale.X, 3);
            Assert.Equal(25, stale.Y, 3);
            Assert.Equal(1.2, stale.Scale, 3);
            Assert.Equal(7, stale.Rotation, 3);
            Assert.Equal(0, pipe.Renderer.Stage.Energy, 3);
        }
    }

    /// <summary>
    /// camera.rotation 的关键帧曲线进舞台镜头（追踪通道全静默）：曲线值是绝对取景而不是叠加，
    /// 撤掉曲线回 CameraConfig 常量。这一环曾经可能是「rotation 恒 0」的旧写法残留，逐环钉住。
    /// </summary>
    [GpuFact]
    public void CameraRotationCurveReachesStageCamera()
    {
        var gpu = D3DDevice.TryCreate(out var error);
        if (gpu is null) GpuRequire.Fail(error);

        using (gpu)
        {
            var scene = PvScene.CreateDefault();
            scene.Camera = new PvCameraConfig { Rotation = 7 };

            using var pipe = new PvPipeline(gpu, scene, new EffectGraph());

            Assert.True(ParamAddress.TryParse("camera.rotation", out var addr), "camera.rotation 应能解析为镜头参数地址");
            var eval = new DirectorEvaluator();
            var curve = new FloatCurve();
            curve.Keys.Add(new CurveKey(0, 0, CurveInterp.Linear));
            curve.Keys.Add(new CurveKey(1, -12.5, CurveInterp.Linear));
            eval.SetCurve(addr, curve);

            pipe.SetDirector(eval);
            pipe.Prepare(0.5);
            Assert.Equal(-6.25, pipe.Renderer.Stage.Camera.Rotation, 3); // 区间中点：曲线值即绝对取景

            pipe.SetDirector(null);
            pipe.Prepare(0.5);
            Assert.Equal(7, pipe.Renderer.Stage.Camera.Rotation, 3);    // 撤掉曲线回常量
        }
    }
}
