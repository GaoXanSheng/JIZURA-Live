namespace JizuraLive.Engine;

/// <summary>
/// 整帧镜头的回填盒：渲染线程每帧把当前 cut 的相机状态写进来，PV 的 VTS 直接驱动线程读最近一帧。
/// 单写者单读者、字段都是 double，最坏读到半新半旧的一帧（模型抖一帧，肉眼无感），不加锁。
/// 渲染侧复用同一个实例、零分配；<see cref="RenderOpts.Camera"/> 不传 = 不回报。
/// </summary>
public sealed class CameraProbe
{
    private double _x, _y, _rot, _s = 1;
    private int _w = 1, _h = 1;
    private int _has;
    private long _at;

    /// <summary>当前是否有效（<see cref="RenderOpts.FrameCamera"/> 关着时渲染侧会置无效）。</summary>
    public bool Has => Volatile.Read(ref _has) == 1;

    /// <summary>渲染线程：回报这一帧用的整帧镜头。w/h = 出图面尺寸（相机 X/Y 同一坐标系）。</summary>
    public void Set(CamState c, int w, int h)
    {
        _x = c.X;
        _y = c.Y;
        _rot = c.Rot;
        _s = c.S;
        _w = Math.Max(1, w);
        _h = Math.Max(1, h);
        Volatile.Write(ref _at, Environment.TickCount64);
        Volatile.Write(ref _has, 1);
    }

    /// <summary>这一帧没有整帧镜头（非 PV 路径 / 无切分）：驱动侧按「无镜头」处理。</summary>
    public void Clear() => Volatile.Write(ref _has, 0);

    /// <summary>
    /// 读最近一帧的镜头（仅当 <paramref name="freshMs"/> 毫秒内更新过才算数——预览停帧时
    /// 不该拿旧值一直推模型）。角度单位 = 度，X/Y 与 w/h 同坐标系（出图像素）。
    /// </summary>
    public bool TryRead(int freshMs, out double x, out double y, out double rot, out double s, out int w, out int h)
    {
        x = y = rot = 0;
        s = 1;
        w = h = 1;
        if (Volatile.Read(ref _has) != 1) return false;
        if (Environment.TickCount64 - Volatile.Read(ref _at) > freshMs) return false;
        w = _w;
        h = _h;
        x = _x;
        y = _y;
        rot = _rot;
        s = _s;
        return true;
    }
}
