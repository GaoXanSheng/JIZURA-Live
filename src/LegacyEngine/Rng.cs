namespace JizuraLive.LegacyEngine;

public sealed class Rng
{
    private uint _s;

    public Rng(uint seed) => _s = seed;

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

        public int Int(int lo, int hi) => (int)Math.Floor(lo + (hi - lo + 1) * Next());

    public T Pick<T>(IReadOnlyList<T> arr) => arr[(int)Math.Floor(Next() * arr.Count) % arr.Count];

    public bool Chance(double p) => Next() < p;

        public T WPick<T>(IReadOnlyList<(T V, double W)> list)
    {
        double tot = 0;
        foreach (var it in list) tot += it.W;
        var x = Next() * tot;
        foreach (var it in list)
            if ((x -= it.W) < 0)                 return it.V;
        return list[^1].V;
    }
}
