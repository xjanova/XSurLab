namespace XSurLab.Core;

/// <summary>
/// Band-limited reader for media whose rate differs from the session rate: a Kaiser-windowed sinc
/// (β = 9, ±32 samples at the lower of the two rates, cutoff 0.455 of the lower rate). When downsampling,
/// the kernel widens with the ratio so content above the new Nyquist limit is removed instead of folding back.
/// </summary>
public sealed class Resampler
{
    private const int HalfWidth = 32, Resolution = 1024;
    private const double Beta = 9, Cutoff = .455;
    private static readonly double[] Kernel = Build();
    private readonly double step;
    private readonly int reach;
    /// <summary>Source frames advanced per target frame.</summary>
    public double Ratio { get; }

    public Resampler(int sourceRate, int targetRate)
    {
        if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate), "Sample rates must be positive.");
        Ratio = (double)sourceRate / targetRate;
        double scale = Math.Max(1, Ratio);
        step = Resolution / scale;
        reach = (int)Math.Ceiling(HalfWidth * scale);
    }

    /// <summary>Writes each channel's value at a fractional source frame; frames outside the media are silence.</summary>
    public void Read(AudioBuffer audio, double position, Span<double> values)
    {
        values.Clear();
        long center = (long)Math.Floor(position);
        double total = 0;
        for (long n = center - reach + 1; n <= center + reach; n++)
        {
            double t = Math.Abs(position - n) * step;
            int i = (int)t;
            if (i >= HalfWidth * Resolution) continue;
            double weight = Kernel[i] + (Kernel[i + 1] - Kernel[i]) * (t - i);
            // Silence outside the media still counts towards the normalisation, so edges fade rather than stretch.
            total += weight;
            if (n < 0 || n >= audio.Frames) continue;
            for (int c = 0; c < values.Length; c++) values[c] += weight * audio[n, c];
        }
        if (total == 0) return;
        for (int c = 0; c < values.Length; c++) values[c] /= total;
    }

    private static double[] Build()
    {
        var table = new double[HalfWidth * Resolution + 2];
        double norm = BesselI0(Beta);
        for (int i = 0; i < HalfWidth * Resolution; i++)
        {
            double x = (double)i / Resolution, r = x / HalfWidth, arg = 2 * Math.PI * Cutoff * x;
            table[i] = (x == 0 ? 1 : Math.Sin(arg) / arg) * BesselI0(Beta * Math.Sqrt(1 - r * r)) / norm;
        }
        return table;
    }
    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, quarter = x * x / 4;
        for (int k = 1; k < 100 && term > sum * 1e-17; k++) { term *= quarter / ((double)k * k); sum += term; }
        return sum;
    }
}
