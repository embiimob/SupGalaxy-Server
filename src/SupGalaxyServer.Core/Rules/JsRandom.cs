using System.Globalization;

namespace SupGalaxyServer.Rules;

/// <summary>
/// Bit-exact ports of SupGalaxy's deterministic random helpers (js/worker.js and js/world-generation.js):
/// makeSeededRandom (FNV-1a seeded mulberry32 style generator), makeNoise (value noise), fbm, modWrap and the
/// V8 Math.hypot algorithm. The server uses them to regenerate exactly the same terrain the browser generates,
/// so it can decide what block is at any position without trusting the players.
/// </summary>
public static class JsRandom
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>A seeded generator returning doubles in [0, 1), identical to SupGalaxy's makeSeededRandom.</summary>
    public sealed class SeededRandom
    {
        private uint _h;

        internal SeededRandom(uint state) => _h = state;

        public double Next()
        {
            unchecked
            {
                _h += 0x6D2B79F5;
                uint t = (_h ^ (_h >> 15)) * (1 | _h);
                t ^= t + (t ^ (t >> 7)) * (61 | t);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    public static SeededRandom MakeSeededRandom(string seed) => new(Fnv(FnvOffset, seed));

    internal static uint Fnv(uint h, ReadOnlySpan<char> s)
    {
        unchecked
        {
            foreach (var c in s) h = (h ^ c) * FnvPrime;
        }
        return h;
    }

    private static uint Fnv(uint h, long value)
    {
        Span<char> buf = stackalloc char[24];
        value.TryFormat(buf, out var n, default, CultureInfo.InvariantCulture);
        return Fnv(h, buf[..n]);
    }

    /// <summary>Port of world-generation.js hashSeed.</summary>
    public static long HashSeed(string seed) => Fnv(FnvOffset, seed) % 16384;

    /// <summary>Value noise identical to SupGalaxy's makeNoise(seed) (worker.js variant).</summary>
    public sealed class Noise
    {
        private readonly uint _prefix;
        private readonly Dictionary<(long, long), double> _cache = new();

        internal Noise(string seed) => _prefix = Fnv(FnvOffset, seed + "|");

        private double Corner(long ix, long iy)
        {
            if (_cache.TryGetValue((ix, iy), out var v)) return v;
            // makeSeededRandom(seed + '|' + ix + ',' + iy)()
            var h = Fnv(_prefix, ix);
            h = Fnv(h, ",");
            h = Fnv(h, iy);
            v = new SeededRandom(h).Next();
            _cache[(ix, iy)] = v;
            return v;
        }

        private static double Interp(double a, double b, double t) => a + (b - a) * (t * (t * (3 - 2 * t)));

        public double Sample(double x, double y)
        {
            var fx0 = Math.Floor(x);
            var fy0 = Math.Floor(y);
            long ix = (long)fx0, iy = (long)fy0;
            double fx = x - fx0, fy = y - fy0;
            double a = Corner(ix, iy), b = Corner(ix + 1, iy), c = Corner(ix, iy + 1), d = Corner(ix + 1, iy + 1);
            double ab = Interp(a, b, fx), cd = Interp(c, d, fx);
            return Interp(ab, cd, fy);
        }
    }

    public static Noise MakeNoise(string seed) => new(seed);

    public static double Fbm(Noise noise, double x, double y, int octaves, double persistence)
    {
        double sum = 0, amp = 1, freq = 1, max = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * noise.Sample(x * freq, y * freq);
            max += amp;
            amp *= persistence;
            freq *= 2;
        }
        return sum / max;
    }

    public static long ModWrap(long value, long size) => ((value % size) + size) % size;

    /// <summary>V8's Math.hypot (Kahan-compensated, max-scaled) so distances match the browser bit for bit.</summary>
    public static double Hypot(params double[] values)
    {
        double max = 0;
        bool nan = false;
        for (int i = 0; i < values.Length; i++)
        {
            var v = Math.Abs(values[i]);
            if (double.IsInfinity(v)) return double.PositiveInfinity;
            if (double.IsNaN(v)) nan = true;
            values[i] = v;
            if (v > max) max = v;
        }
        if (nan) return double.NaN;
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        foreach (var v in values)
        {
            double n = v / max;
            double summand = n * n - compensation;
            double preliminary = sum + summand;
            compensation = (preliminary - sum) - summand;
            sum = preliminary;
        }
        return Math.Sqrt(sum) * max;
    }
}
