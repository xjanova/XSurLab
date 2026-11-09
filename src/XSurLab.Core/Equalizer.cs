namespace XSurLab.Core;

public enum EqBandKind { HighPass, LowShelf, Peak, HighShelf, LowPass }

/// <summary>One EQ band. Gain applies to shelves and peaks; Q to peaks and the 12 dB/oct high- and low-pass.</summary>
public sealed record EqBand(bool On, double Frequency, double GainDb = 0, double Q = .7071);

/// <summary>Fixed channel-strip EQ in processing order: high-pass, low shelf, two peaks, high shelf, low-pass.</summary>
public sealed record TrackEq
{
    public const int Count = 6;
    public static readonly TrackEq Flat = new();
    public static readonly EqBandKind[] Kinds = [EqBandKind.HighPass, EqBandKind.LowShelf, EqBandKind.Peak, EqBandKind.Peak, EqBandKind.HighShelf, EqBandKind.LowPass];
    public static readonly string[] Names = ["High-pass", "Low shelf", "Peak 1", "Peak 2", "High shelf", "Low-pass"];

    public EqBand HighPass { get; init; } = new(false, 80);
    public EqBand LowShelf { get; init; } = new(false, 120);
    public EqBand Peak1 { get; init; } = new(false, 500, 0, 1);
    public EqBand Peak2 { get; init; } = new(false, 3000, 0, 1);
    public EqBand HighShelf { get; init; } = new(false, 8000);
    public EqBand LowPass { get; init; } = new(false, 18000);

    public EqBand Band(int index) => index switch
    {
        0 => HighPass, 1 => LowShelf, 2 => Peak1, 3 => Peak2, 4 => HighShelf, 5 => LowPass,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    public TrackEq WithBand(int index, EqBand band) => index switch
    {
        0 => this with { HighPass = band }, 1 => this with { LowShelf = band }, 2 => this with { Peak1 = band },
        3 => this with { Peak2 = band }, 4 => this with { HighShelf = band }, 5 => this with { LowPass = band },
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public static bool HasGain(int index) => Kinds[index] is EqBandKind.LowShelf or EqBandKind.Peak or EqBandKind.HighShelf;
    public static bool HasQ(int index) => Kinds[index] is EqBandKind.Peak or EqBandKind.HighPass or EqBandKind.LowPass;

    public Biquad[] Design(int sampleRate) => Enumerable.Range(0, Count).Select(i => Biquad.Design(Kinds[i], Band(i), sampleRate)).ToArray();

    /// <summary>Magnitude in dB of the whole EQ at a frequency.</summary>
    public double ResponseDb(double frequency, int sampleRate) => Design(sampleRate).Sum(b => b.MagnitudeDb(frequency, sampleRate));
}

/// <summary>Normalised biquad (a0 = 1) after the RBJ audio-EQ cookbook. Shelves use slope S = 1.</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly Biquad Identity = new(1, 0, 0, 0, 0);
    public bool IsIdentity => this == Identity;

    public static Biquad Design(EqBandKind kind, EqBand band, int sampleRate)
    {
        if (!band.On || (kind is not (EqBandKind.HighPass or EqBandKind.LowPass) && band.GainDb == 0)) return Identity;
        // Keep the corner below Nyquist at every session rate (20 kHz at 44.1 kHz would otherwise fold).
        double w = 2 * Math.PI * Math.Min(band.Frequency, .45 * sampleRate) / sampleRate, cos = Math.Cos(w), sin = Math.Sin(w);
        double a = Math.Pow(10, band.GainDb / 40), alpha = sin / (2 * band.Q), shelf = sin / Math.Sqrt(2) * 2 * Math.Sqrt(a);
        (double b0, double b1, double b2, double a0, double a1, double a2) = kind switch
        {
            EqBandKind.HighPass => ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
            EqBandKind.LowPass => ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
            EqBandKind.Peak => (1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a),
            EqBandKind.LowShelf => (a * (a + 1 - (a - 1) * cos + shelf), 2 * a * (a - 1 - (a + 1) * cos), a * (a + 1 - (a - 1) * cos - shelf),
                a + 1 + (a - 1) * cos + shelf, -2 * (a - 1 + (a + 1) * cos), a + 1 + (a - 1) * cos - shelf),
            _ => (a * (a + 1 + (a - 1) * cos + shelf), -2 * a * (a - 1 + (a + 1) * cos), a * (a + 1 + (a - 1) * cos - shelf),
                a + 1 - (a - 1) * cos + shelf, 2 * (a - 1 - (a + 1) * cos), a + 1 - (a - 1) * cos - shelf),
        };
        return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public double MagnitudeDb(double frequency, int sampleRate)
    {
        if (IsIdentity) return 0;
        double w = 2 * Math.PI * frequency / sampleRate, c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = B0 + B1 * c1 + B2 * c2, ni = B1 * s1 + B2 * s2, dr = 1 + A1 * c1 + A2 * c2, di = A1 * s1 + A2 * s2;
        return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
    }
}

/// <summary>
/// Runs a track's six biquads on every source channel (direct form I). New coefficients move linearly over the ramp,
/// which stays stable because the stable region of a biquad's (a1, a2) is convex. Identity bands only keep their history.
/// </summary>
internal sealed class Equalizer
{
    private const int Taps = 5;
    private readonly int channels;
    private readonly double[] current = new double[TrackEq.Count * Taps], target = new double[TrackEq.Count * Taps], step = new double[TrackEq.Count * Taps];
    // Per band and channel: x1, x2, y1, y2.
    private readonly double[] history;
    private readonly bool[] active = new bool[TrackEq.Count];
    private int rampLeft;

    public Equalizer(int channels)
    {
        this.channels = channels;
        history = new double[TrackEq.Count * channels * 4];
    }

    public void Reset(Biquad[] bands)
    {
        Load(bands, target);
        Array.Copy(target, current, current.Length);
        rampLeft = 0;
        for (int b = 0; b < TrackEq.Count; b++) active[b] = !bands[b].IsIdentity;
    }

    public void Retarget(Biquad[] bands, int frames)
    {
        Load(bands, target);
        for (int i = 0; i < current.Length; i++) step[i] = (target[i] - current[i]) / frames;
        for (int b = 0; b < TrackEq.Count; b++) active[b] |= !bands[b].IsIdentity;
        rampLeft = frames;
    }

    public void Step()
    {
        if (rampLeft == 0) return;
        if (--rampLeft > 0) { for (int i = 0; i < current.Length; i++) current[i] += step[i]; return; }
        Array.Copy(target, current, current.Length);
        for (int b = 0; b < TrackEq.Count; b++) active[b] = !IsIdentity(target, b);
    }

    public void Process(Span<double> frame)
    {
        for (int b = 0; b < TrackEq.Count; b++)
        {
            int k = b * Taps;
            for (int c = 0; c < channels; c++)
            {
                int h = (b * channels + c) * 4;
                double x = frame[c], y = active[b]
                    ? current[k] * x + current[k + 1] * history[h] + current[k + 2] * history[h + 1] - current[k + 3] * history[h + 2] - current[k + 4] * history[h + 3]
                    : x;
                history[h + 1] = history[h]; history[h] = x;
                history[h + 3] = history[h + 2]; history[h + 2] = y;
                frame[c] = y;
            }
        }
    }

    private static void Load(Biquad[] bands, double[] into)
    {
        for (int b = 0; b < TrackEq.Count; b++)
        {
            var q = bands[b];
            into[b * Taps] = q.B0; into[b * Taps + 1] = q.B1; into[b * Taps + 2] = q.B2; into[b * Taps + 3] = q.A1; into[b * Taps + 4] = q.A2;
        }
    }
    private static bool IsIdentity(double[] c, int b) => c[b * Taps] == 1 && c[b * Taps + 1] == 0 && c[b * Taps + 2] == 0 && c[b * Taps + 3] == 0 && c[b * Taps + 4] == 0;
}
