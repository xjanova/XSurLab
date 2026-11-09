namespace XSurLab.Core;

/// <summary>
/// Band-limited pink noise at −20 dBFS RMS on one speaker at a time, for setting trims with an SPL meter at the listening
/// position. Main speakers get 500 Hz–2 kHz, the subwoofer 20–120 Hz. It never finishes; switching speakers fades out and
/// back in over 20 ms.
/// </summary>
public sealed class TestSignal : IAudioSource
{
    public const double LevelDb = -20;
    /// <summary>No sample goes beyond this, 12 dB above the RMS level.</summary>
    public const double PeakLimit = .4;
    private readonly Speaker[] speakers;
    private readonly Noise main, sub;
    private readonly int rampFrames;
    private int requested = -1, playing = -1;
    private double envelope;

    public int SampleRate { get; }
    public int Channels => speakers.Length;
    public uint ChannelMask { get; }
    public SpeakerLayout Layout { get; }
    public bool SeekPending => false;
    public int Seeks => 0;
    public bool Finished => false;

    /// <summary>Speaker index that plays the noise, or −1 for silence. Safe to set from any thread.</summary>
    public int SpeakerIndex
    {
        get => Volatile.Read(ref requested);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -1);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, speakers.Length);
            Volatile.Write(ref requested, value);
        }
    }

    public TestSignal(int sampleRate, SpeakerLayout layout, int seed = 1)
    {
        if (sampleRate is not (44100 or 48000 or 96000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        Layout = layout;
        speakers = SpatialMixer.Speakers(layout);
        ChannelMask = SpatialMixer.ChannelMask(layout);
        rampFrames = Math.Max(1, (int)Math.Round(sampleRate * StreamingMixer.RampSeconds));
        main = new Noise(sampleRate, 500, 2000, seed);
        sub = new Noise(sampleRate, 20, 120, seed + 1);
    }

    /// <summary>Clockwise as a listener turns: the ear-level ring from L, then the heights from Tfl, the subwoofer last.</summary>
    public static int[] Order(SpeakerLayout layout)
    {
        var speakers = SpatialMixer.Speakers(layout);
        return speakers.Select((s, i) => (s, i))
            .OrderBy(x => x.s.IsLfe).ThenBy(x => x.s.Elevation)
            .ThenBy(x => ((x.s.Azimuth + (x.s.Elevation > 0 ? 45 : 30)) % 360 + 360) % 360)
            .Select(x => x.i).ToArray();
    }

    public int Read(Span<double> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("Buffer must contain whole audio frames.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        destination.Clear();
        int frames = destination.Length / Channels, wanted = SpeakerIndex;
        for (int f = 0; f < frames; f++)
        {
            // A new speaker waits until the old one has faded out, so the noise never jumps between channels.
            if (wanted != playing)
            {
                envelope -= 1d / rampFrames;
                if (envelope <= 0) { envelope = 0; playing = wanted; }
            }
            else if (playing >= 0 && envelope < 1) envelope = Math.Min(1, envelope + 1d / rampFrames);
            double m = main.Next(), s = sub.Next();
            if (playing >= 0 && envelope > 0) destination[f * Channels + playing] = (speakers[playing].IsLfe ? s : m) * envelope;
        }
        return frames;
    }

    /// <summary>White noise → Paul Kellet's pink filter → 24 dB/oct band-pass, scaled to the RMS level by a measured warm-up.</summary>
    private sealed class Noise
    {
        private ulong state;
        private double b0, b1, b2, b3, b4, b5, b6, scale;
        private readonly Biquad[] filters;
        private readonly double[] history;

        public Noise(int rate, double lowHz, double highHz, int seed)
        {
            state = 0x9E3779B97F4A7C15UL * (ulong)(seed + 1);
            var q = Math.Sqrt(.5);
            var hp = Biquad.Design(EqBandKind.HighPass, new EqBand(true, lowHz, 0, q), rate);
            var lp = Biquad.Design(EqBandKind.LowPass, new EqBand(true, highHz, 0, q), rate);
            filters = [hp, hp, lp, lp];
            history = new double[filters.Length * 4];
            // One second lets the filters settle; the next ten measure the unscaled level the scale corrects.
            for (int i = 0; i < rate; i++) Raw();
            double power = 0;
            int count = rate * 10;
            for (int i = 0; i < count; i++) { double x = Raw(); power += x * x; }
            scale = Math.Pow(10, LevelDb / 20) / Math.Sqrt(power / count);
        }

        public double Next() => Math.Clamp(Raw() * scale, -PeakLimit, PeakLimit);

        private double Raw()
        {
            // xorshift64*: deterministic, so every run and every test hears the same noise.
            state ^= state >> 12; state ^= state << 25; state ^= state >> 27;
            double white = (long)((state * 0x2545F4914F6CDD1DUL) >> 11) / (double)(1L << 52) - 1;
            b0 = .99886 * b0 + white * .0555179; b1 = .99332 * b1 + white * .0750759; b2 = .969 * b2 + white * .153852;
            b3 = .8665 * b3 + white * .3104856; b4 = .55 * b4 + white * .5329522; b5 = -.7616 * b5 - white * .016898;
            double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * .5362;
            b6 = white * .115926;
            for (int i = 0; i < filters.Length; i++)
            {
                var q = filters[i];
                int h = i * 4;
                double y = q.B0 * pink + q.B1 * history[h] + q.B2 * history[h + 1] - q.A1 * history[h + 2] - q.A2 * history[h + 3];
                history[h + 1] = history[h]; history[h] = pink;
                history[h + 3] = history[h + 2]; history[h + 2] = y;
                pink = y;
            }
            return pink;
        }
    }
}
