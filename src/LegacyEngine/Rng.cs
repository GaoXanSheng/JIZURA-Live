namespace JizuraLive.LegacyEngine;

/// <summary>
/// mulberry32 种子随机流（移植自 01_util.js 的 J.rng）。
/// 位级语义与 JS 一致：同一 seed 产出同一序列，表现包动画因此逐帧可复现。
/// </summary>
public sealed class Rng
{
    private uint _s;

    public Rng(uint seed) => _s = seed;

    /// <summary>取下一个 [0,1) 随机数。逐位对齐 JS：所有整型运算 32 位回绕，&gt;&gt;&gt; 即 (uint) 右移。</summary>
    public double Next()
    {
        unchecked
        {
            _s = _s + 0x6d2b79f5;
            uint t = _s;
            t = (uint)Imul((int)(t ^ (t >> 15)), (int)(t | 1));
            t ^= (uint)(Imul((int)(t ^ (t >> 7)), (int)(t | 61)) + (int)t);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    private static int Imul(int a, int b) => unchecked(a * b);

    public double Range(double lo, double hi) => lo + (hi - lo) * Next();

    /// <summary>[lo, hi] 闭区间整数（对齐 JS Math.floor(lo + (hi-lo+1)*f())）。</summary>
    public int Int(int lo, int hi) => (int)Math.Floor(lo + (hi - lo + 1) * Next());

    public T Pick<T>(IReadOnlyList<T> arr) => arr[(int)Math.Floor(Next() * arr.Count) % arr.Count];

    public bool Chance(double p) => Next() < p;

    /// <summary>加权抽取：元素为 (value, weight) 对；对齐 JS wpick 的「权重和为 0/负时取最后」行为。</summary>
    public T WPick<T>(IReadOnlyList<(T V, double W)> list)
    {
        double tot = 0;
        foreach (var it in list) tot += it.W;
        var x = Next() * tot;
        foreach (var it in list)
            if ((x -= it.W) < 0) // 严格小于：权重和为 0 时不中途命中，落到末尾的「取最后」分支
                return it.V;
        return list[^1].V;
    }
}
