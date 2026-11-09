namespace XSurLab.Core;

/// <summary>
/// Speaker calibration on the way to the monitor: bass management, then each speaker's trim and delay. It wraps the mixer
/// (or a test signal) and is never part of an export. A flat calibration passes every sample through unchanged.
/// </summary>
/// <remarks>
/// Bass management is a fourth-order Linkwitz–Riley crossover: every main speaker is high-passed, the low-passed sum of the
/// mains joins LFE on the subwoofer, and the two halves add back to a flat magnitude. Changes ramp over 20 ms like the mixer;
/// a delay change crossfades from the old to the new tap. When the source ends, the delayed speakers play out their delay.
/// </remarks>
public sealed class MonitorChain : IAudioSource
{
    private const int Taps = 5;
    private readonly IAudioSource source;
    private readonly int channels, lfe, rampFrames, lineLength;
    private readonly bool canBass;
    private SpeakerCalibration published;
    private SpeakerCalibration active;
    private readonly double[] gain, gainStep, frame;
    // The tap heard now, the tap it is fading from, and the tap the calibration asks for. A change that arrives during a
    // crossfade waits for it to end, so there are never more than two taps and never a jump.
    private readonly int[] delay, previousDelay, targetDelay;
    private readonly double[] lines;
    private int write, delayFade, rampLeft, lastSeeks, tailLeft = -1;
    private bool delayPending;
    private double bass, bassStep, lfeGain, lfeGainStep;
    // Crossover coefficients (b0, b1, b2, a1, a2) for the high- and low-pass halves, ramped like the track EQ.
    private readonly double[] high = new double[Taps], highTarget = new double[Taps], highStep = new double[Taps];
    private readonly double[] low = new double[Taps], lowTarget = new double[Taps], lowStep = new double[Taps];
    // Two cascaded sections per main speaker, then two for the summed low end: x1, x2, y1, y2 each.
    private readonly double[] highHistory, lowHistory = new double[8];

    public int SampleRate => source.SampleRate;
    public int Channels => channels;
    public uint ChannelMask => source.ChannelMask;
    public bool SeekPending => source.SeekPending;
    public int Seeks => source.Seeks;
    public SpeakerLayout Layout { get; }
    /// <summary>Calibration being applied; a new one ramps in from the next read.</summary>
    public SpeakerCalibration Calibration => Volatile.Read(ref published);
    /// <summary>Test signals are measured at each speaker, so they skip bass management and only take trim and delay.</summary>
    public bool BypassBassManagement { get; }

    /// <param name="layout">Speakers of the source's channels: the session layout, or Stereo for the fold-down.</param>
    public MonitorChain(IAudioSource source, SpeakerLayout layout, SpeakerCalibration? calibration = null, bool bypassBassManagement = false)
    {
        var speakers = SpatialMixer.Speakers(layout);
        if (source.Channels != speakers.Length || source.ChannelMask != SpatialMixer.ChannelMask(layout))
            throw new ArgumentException("The source does not carry this layout's channels.", nameof(source));
        this.source = source;
        Layout = layout;
        BypassBassManagement = bypassBassManagement;
        channels = speakers.Length;
        lfe = Array.FindIndex(speakers, s => s.IsLfe);
        canBass = lfe >= 0 && !bypassBassManagement;
        rampFrames = Math.Max(1, (int)Math.Round(SampleRate * StreamingMixer.RampSeconds));
        lineLength = Frames(SpeakerCalibration.MaxDelayMs) + 1;
        lines = new double[channels * lineLength];
        gain = new double[channels]; gainStep = new double[channels]; frame = new double[channels];
        delay = new int[channels]; previousDelay = new int[channels]; targetDelay = new int[channels];
        highHistory = new double[channels * 8];
        lastSeeks = source.Seeks;
        published = active = Check(calibration ?? SpeakerCalibration.Flat(layout));
        Settle();
    }

    /// <summary>Publishes a new calibration from any thread.</summary>
    public void Update(SpeakerCalibration calibration) => Volatile.Write(ref published, Check(calibration));

    private SpeakerCalibration Check(SpeakerCalibration calibration)
    {
        // The output it was made for is the caller's business; the speakers and values must fit this layout.
        CalibrationStore.Validate(calibration with { DeviceId = calibration.DeviceId is { Length: > 0 } id ? id : "-" });
        if (calibration.Layout != Layout) throw new ArgumentException($"This calibration is for {calibration.Layout}, not {Layout}.", nameof(calibration));
        return calibration;
    }

    private int Frames(double ms) => (int)Math.Round(ms * SampleRate / 1000);
    private double BassTarget => canBass && active.BassManagement ? 1 : 0;
    private double LfeTarget => active.LfeGain ? Math.Pow(10, 10 / 20d) : 1;
    private double GainTarget(int c) => active.Speakers[c].TrimDb == 0 ? 1 : Math.Pow(10, active.Speakers[c].TrimDb / 20);

    // Jumps straight to the active calibration: at the start, before any audio has been heard.
    private void Settle()
    {
        for (int c = 0; c < channels; c++) { gain[c] = GainTarget(c); delay[c] = previousDelay[c] = targetDelay[c] = Frames(active.Speakers[c].DelayMs); }
        bass = BassTarget; lfeGain = LfeTarget;
        Design(active.CrossoverHz);
        Array.Copy(highTarget, high, Taps); Array.Copy(lowTarget, low, Taps);
        rampLeft = delayFade = 0;
        delayPending = false;
    }

    private void Retarget(SpeakerCalibration calibration)
    {
        active = calibration;
        delayPending = false;
        for (int c = 0; c < channels; c++)
        {
            gainStep[c] = (GainTarget(c) - gain[c]) / rampFrames;
            targetDelay[c] = Frames(calibration.Speakers[c].DelayMs);
            delayPending |= targetDelay[c] != delay[c];
        }
        bassStep = (BassTarget - bass) / rampFrames;
        lfeGainStep = (LfeTarget - lfeGain) / rampFrames;
        Design(calibration.CrossoverHz);
        for (int i = 0; i < Taps; i++) { highStep[i] = (highTarget[i] - high[i]) / rampFrames; lowStep[i] = (lowTarget[i] - low[i]) / rampFrames; }
        rampLeft = rampFrames;
    }

    private void Design(int crossover)
    {
        static void Load(Biquad q, double[] into) { into[0] = q.B0; into[1] = q.B1; into[2] = q.B2; into[3] = q.A1; into[4] = q.A2; }
        // Two Butterworth sections (Q = 1/√2) in a row give the Linkwitz–Riley slope of 24 dB per octave.
        Load(Biquad.Design(EqBandKind.HighPass, new EqBand(true, crossover, 0, Math.Sqrt(.5)), SampleRate), highTarget);
        Load(Biquad.Design(EqBandKind.LowPass, new EqBand(true, crossover, 0, Math.Sqrt(.5)), SampleRate), lowTarget);
    }

    private void Step()
    {
        if (--rampLeft == 0)
        {
            for (int c = 0; c < channels; c++) gain[c] = GainTarget(c);
            bass = BassTarget; lfeGain = LfeTarget;
            Array.Copy(highTarget, high, Taps); Array.Copy(lowTarget, low, Taps);
            return;
        }
        for (int c = 0; c < channels; c++) gain[c] += gainStep[c];
        bass += bassStep; lfeGain += lfeGainStep;
        for (int i = 0; i < Taps; i++) { high[i] += highStep[i]; low[i] += lowStep[i]; }
    }

    /// <summary>Forgets delayed audio and filter history, as after a seek: the mixer fades in from silence there.</summary>
    private void Clear()
    {
        Array.Clear(lines); Array.Clear(highHistory); Array.Clear(lowHistory);
        for (int c = 0; c < channels; c++) delay[c] = previousDelay[c] = targetDelay[c];
        delayFade = 0;
        delayPending = false;
    }

    private void StartDelayFade()
    {
        for (int c = 0; c < channels; c++) { previousDelay[c] = delay[c]; delay[c] = targetDelay[c]; }
        delayFade = rampFrames;
        delayPending = false;
    }

    /// <summary>Call on the audio thread, like Read: it depends on the delays being played.</summary>
    public bool Finished => source.Finished && (tailLeft == 0 || tailLeft < 0 && Longest() == 0);
    private int Longest()
    {
        int longest = 0;
        // The tap being faded out only matters while the crossfade runs.
        for (int c = 0; c < channels; c++) longest = Math.Max(longest, Math.Max(targetDelay[c], Math.Max(delay[c], delayFade > 0 ? previousDelay[c] : 0)));
        return longest;
    }

    public int Read(Span<double> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length % channels != 0) throw new ArgumentException("Buffer must contain whole audio frames.", nameof(destination));
        int frames = source.Read(destination, cancellationToken);
        int seeks = source.Seeks;
        if (seeks != lastSeeks) { lastSeeks = seeks; tailLeft = -1; Clear(); }
        // A calibration published for this block counts towards its tail.
        var block = Volatile.Read(ref published);
        if (!ReferenceEquals(block, active)) Retarget(block);
        // After the source ends, silence runs through the delay lines until the most delayed speaker has played out.
        if (source.Finished && frames < destination.Length / channels)
        {
            if (tailLeft < 0) tailLeft = Longest();
            int extra = Math.Min(tailLeft, destination.Length / channels - frames);
            tailLeft -= extra;
            frames += extra;
        }
        for (int f = 0; f < frames; f++)
        {
            if (rampLeft > 0) Step();
            var samples = destination.Slice(f * channels, channels);
            samples.CopyTo(frame);
            if (canBass) Manage();
            if (delayFade == 0 && delayPending) StartDelayFade();
            double fade = delayFade > 0 ? (double)delayFade-- / rampFrames : 0;
            for (int c = 0; c < channels; c++)
            {
                int line = c * lineLength;
                lines[line + write] = frame[c] * gain[c];
                double value = lines[line + (write - delay[c] + lineLength) % lineLength];
                if (fade > 0) value += fade * (lines[line + (write - previousDelay[c] + lineLength) % lineLength] - value);
                samples[c] = value;
            }
            write = (write + 1) % lineLength;
        }
        return frames;
    }

    // Filters run whether or not bass management is on, so switching it on has no start-up transient.
    // With bass at 0 every speaker keeps its own sample exactly.
    private void Manage()
    {
        double sum = 0, kept = frame[lfe];
        for (int c = 0; c < channels; c++)
        {
            if (c == lfe) continue;
            double x = frame[c];
            sum += x;
            double y = Section(high, highHistory, c * 8, Section(high, highHistory, c * 8 + 4, x));
            if (bass != 0) frame[c] = x + bass * (y - x);
        }
        double sub = Section(low, lowHistory, 0, Section(low, lowHistory, 4, sum));
        if (bass != 0) frame[lfe] = kept + bass * (kept * lfeGain + sub - kept);
    }

    private static double Section(double[] q, double[] history, int h, double x)
    {
        double y = q[0] * x + q[1] * history[h] + q[2] * history[h + 1] - q[3] * history[h + 2] - q[4] * history[h + 3];
        history[h + 1] = history[h]; history[h] = x;
        history[h + 3] = history[h + 2]; history[h + 2] = y;
        return y;
    }
}
