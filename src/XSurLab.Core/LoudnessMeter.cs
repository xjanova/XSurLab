namespace XSurLab.Core;

/// <summary>Loudness and peaks of a finished stream. LUFS values are −∞ when nothing passes the gates.</summary>
public sealed record LoudnessReport(double Integrated, double Range, double MaxMomentary, double MaxShortTerm,
    double TruePeak, double SamplePeak, double[] ChannelPeaks)
{
    public double TruePeakDb => LoudnessMeter.Db(TruePeak);
    public double SamplePeakDb => LoudnessMeter.Db(SamplePeak);
}

/// <summary>Live meter values, published after every 100 ms of audio. Channel peaks cover that 100 ms only.</summary>
public sealed record MeterReading(double Momentary, double ShortTerm, double Integrated, double TruePeak, double[] ChannelPeaks, long Frames)
{
    public double TruePeakDb => LoudnessMeter.Db(TruePeak);
}

/// <summary>
/// ITU-R BS.1770-4 loudness (K-weighting, 400 ms blocks every 100 ms, −70 LUFS absolute and −10 LU relative gates),
/// EBU Tech 3342 loudness range from 3 s short-term values (−20 LU relative gate, 10th–95th percentile) and a
/// 4× (2× from 96 kHz) oversampled true-peak estimate. Call <see cref="Process"/> from one thread.
/// </summary>
public sealed class LoudnessMeter
{
    private static readonly double AbsoluteGate = Math.Pow(10, (-70 + .691) / 10);
    private readonly int channels, segmentFrames;
    private readonly double[] weights, energy, segmentPeak, channelPeak;
    private readonly Biquad[] stage1, stage2;
    private readonly TruePeak[]? truePeaks;
    private readonly double[] recent = new double[30];
    private readonly List<double> blocks = [], shortTerm = [];
    private int segmentFill, segments;
    private double maxMomentary = double.NegativeInfinity, maxShortTerm = double.NegativeInfinity, truePeak;
    private MeterReading? latest;

    public LoudnessMeter(int sampleRate, IReadOnlyList<Speaker> speakers, bool measureTruePeak = true)
    {
        if (sampleRate <= 0 || sampleRate % 10 != 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "The meter needs a whole number of frames per 100 ms.");
        if (speakers.Count == 0) throw new ArgumentException("A meter needs at least one channel.", nameof(speakers));
        channels = speakers.Count;
        segmentFrames = sampleRate / 10;
        weights = speakers.Select(Weight).ToArray();
        var (b1, a1, b2, a2) = KWeighting(sampleRate);
        stage1 = Enumerable.Range(0, channels).Select(_ => new Biquad(b1, a1)).ToArray();
        stage2 = Enumerable.Range(0, channels).Select(_ => new Biquad(b2, a2)).ToArray();
        if (measureTruePeak)
        {
            var phases = TruePeak.Design(sampleRate < 96000 ? 4 : 2);
            truePeaks = Enumerable.Range(0, channels).Select(_ => new TruePeak(phases)).ToArray();
        }
        energy = new double[channels]; segmentPeak = new double[channels]; channelPeak = new double[channels];
    }

    public long Frames { get; private set; }
    public MeterReading? Latest => Volatile.Read(ref latest);

    /// <summary>BS.1770-4 channel weight: 0 for LFE, 1.41 between ±60° and ±120° below 30° elevation, otherwise 1.</summary>
    public static double Weight(Speaker speaker) => speaker.IsLfe ? 0
        : speaker.Elevation < 30 && Math.Abs(speaker.Azimuth) is >= 60 and <= 120 ? 1.41 : 1;

    /// <summary>K-weighting pre-filter and RLB high-pass for any rate (the formulation used by libebur128).</summary>
    public static (double[] B1, double[] A1, double[] B2, double[] A2) KWeighting(int sampleRate)
    {
        double f0 = 1681.974450955533, g = 3.999843853973347, q = .7071752369554196;
        double k = Math.Tan(Math.PI * f0 / sampleRate), vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, .4996667741545416);
        double a0 = 1 + k / q + k * k;
        double[] b1 = [(vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0];
        double[] a1 = [1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0];
        f0 = 38.13547087602444; q = .5003270373238773;
        k = Math.Tan(Math.PI * f0 / sampleRate);
        a0 = 1 + k / q + k * k;
        return (b1, a1, [1, -2, 1], [1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0]);
    }

    public void Process(ReadOnlySpan<double> interleaved)
    {
        if (interleaved.Length % channels != 0) throw new ArgumentException("Buffer must contain whole audio frames.", nameof(interleaved));
        for (int f = 0; f < interleaved.Length; f += channels)
        {
            for (int c = 0; c < channels; c++)
            {
                double x = interleaved[f + c], magnitude = Math.Abs(x);
                if (magnitude > segmentPeak[c]) segmentPeak[c] = magnitude;
                if (truePeaks is not null) truePeak = Math.Max(truePeak, truePeaks[c].Process(x));
                if (weights[c] == 0) continue;
                double weighted = stage2[c].Process(stage1[c].Process(x));
                energy[c] += weighted * weighted;
            }
            Frames++;
            if (++segmentFill == segmentFrames) CloseSegment();
        }
    }

    public LoudnessReport Report()
    {
        var peaks = channelPeak.Zip(segmentPeak, Math.Max).ToArray();
        double samplePeak = peaks.Max();
        return new(Gated(blocks), Range(shortTerm), maxMomentary, maxShortTerm, Math.Max(truePeak, samplePeak), samplePeak, peaks);
    }

    private void CloseSegment()
    {
        double power = 0;
        for (int c = 0; c < channels; c++) { power += weights[c] * energy[c] / segmentFrames; energy[c] = 0; }
        recent[segments++ % recent.Length] = power;
        segmentFill = 0;
        double momentary = double.NegativeInfinity, shortValue = double.NegativeInfinity;
        if (segments >= 4)
        {
            double block = Mean(4);
            blocks.Add(block);
            momentary = Lufs(block); maxMomentary = Math.Max(maxMomentary, momentary);
        }
        if (segments >= 30)
        {
            double window = Mean(30);
            shortTerm.Add(window);
            shortValue = Lufs(window); maxShortTerm = Math.Max(maxShortTerm, shortValue);
        }
        var peaks = (double[])segmentPeak.Clone();
        for (int c = 0; c < channels; c++) channelPeak[c] = Math.Max(channelPeak[c], segmentPeak[c]);
        Array.Clear(segmentPeak);
        double peak = Math.Max(truePeak, channelPeak.Max());
        Volatile.Write(ref latest, new MeterReading(momentary, shortValue, Gated(blocks), peak, peaks, Frames));
    }
    private double Mean(int count)
    {
        double sum = 0;
        for (int i = 1; i <= count; i++) sum += recent[(segments - i) % recent.Length];
        return sum / count;
    }

    internal static double Db(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;
    private static double Lufs(double power) => power > 0 ? -.691 + 10 * Math.Log10(power) : double.NegativeInfinity;

    private static double Gated(List<double> powers)
    {
        double sum = 0; int count = 0;
        foreach (var p in powers) if (p > AbsoluteGate) { sum += p; count++; }
        if (count == 0) return double.NegativeInfinity;
        double relative = sum / count * .1;
        sum = 0; count = 0;
        foreach (var p in powers) if (p > AbsoluteGate && p > relative) { sum += p; count++; }
        return count == 0 ? double.NegativeInfinity : Lufs(sum / count);
    }
    private static double Range(List<double> powers)
    {
        var passed = powers.Where(p => p > AbsoluteGate).ToArray();
        if (passed.Length == 0) return 0;
        double relative = passed.Average() * .01;
        var loudness = passed.Where(p => p > relative).Select(Lufs).Order().ToArray();
        if (loudness.Length == 0) return 0;
        int low = (int)((loudness.Length - 1) * .10 + .5), high = (int)((loudness.Length - 1) * .95 + .5);
        return loudness[high] - loudness[low];
    }

    private sealed class Biquad(double[] b, double[] a)
    {
        private readonly double b0 = b[0], b1 = b[1], b2 = b[2], a1 = a[1], a2 = a[2];
        private double z1, z2;
        public double Process(double x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }

    // Polyphase Kaiser-windowed sinc interpolator; the reading is the largest of the samples and every phase.
    private sealed class TruePeak(double[][] phases)
    {
        private const int Taps = 32;
        private readonly double[] history = new double[Taps * 2];
        private int position;
        public static double[][] Design(int factor)
        {
            int length = factor * Taps;
            double centre = (length - 1) / 2d, cutoff = .48 / factor, norm = I0(8);
            var prototype = new double[length];
            for (int k = 0; k < length; k++)
            {
                double x = k - centre, arg = 2 * Math.PI * cutoff * x, r = x / (centre + 1);
                prototype[k] = (arg == 0 ? 1 : Math.Sin(arg) / arg) * I0(8 * Math.Sqrt(1 - r * r)) / norm;
            }
            return Enumerable.Range(0, factor).Select(p =>
            {
                var phase = Enumerable.Range(0, Taps).Select(j => prototype[p + factor * j]).ToArray();
                double sum = phase.Sum();
                return phase.Select(v => v / sum).ToArray();
            }).ToArray();
        }
        public double Process(double x)
        {
            position = (position + Taps - 1) % Taps;
            history[position] = history[position + Taps] = x;
            double peak = Math.Abs(x);
            foreach (var phase in phases)
            {
                double y = 0;
                for (int j = 0; j < Taps; j++) y += phase[j] * history[position + j];
                peak = Math.Max(peak, Math.Abs(y));
            }
            return peak;
        }
        private static double I0(double x)
        {
            double sum = 1, term = 1, quarter = x * x / 4;
            for (int k = 1; k < 100 && term > sum * 1e-17; k++) { term *= quarter / ((double)k * k); sum += term; }
            return sum;
        }
    }
}
