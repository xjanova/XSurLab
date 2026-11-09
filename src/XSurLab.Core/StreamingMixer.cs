namespace XSurLab.Core;

public sealed class StreamingMixer
{
    private const double RampSeconds = .02;
    private readonly SessionDocument initial;
    private readonly TrackState[] states;
    private readonly double[] mix;
    private readonly double[] stereo = new double[2];
    private readonly int lfeChannel;
    private Parameters parameters, active;
    private double master, masterStep;
    private int rampLeft;
    public int SampleRate => initial.SampleRate;
    public SpeakerLayout Layout => initial.Layout;
    public bool StereoMonitor { get; }
    public int Channels { get; }
    public long TotalFrames { get; }
    public long Position { get; private set; }
    public double Peak { get; private set; }
    /// <summary>Frames over which a live parameter change moves from the old to the new value.</summary>
    public int RampFrames { get; }
    /// <summary>Loudness and peaks of everything read so far, measured on the mixer output.</summary>
    public LoudnessMeter Meter { get; }

    public StreamingMixer(SessionDocument session, IReadOnlyList<LoadedTrack> tracks, bool stereoMonitor = false)
    {
        SessionStore.Validate(session);
        if (!session.Tracks.SequenceEqual(tracks.Select(t => t.Settings)))
            throw new InvalidDataException("Loaded media does not match the session.");
        if (!tracks.Any(t => !t.Settings.Mute && (!tracks.Any(x => x.Settings.Solo) || t.Settings.Solo)))
            throw new InvalidOperationException("No audible tracks. Import audio or check mute/solo.");
        if (tracks.Any(t => t.Audio.SampleRate <= 0 || t.Audio.Channels is not (1 or 2) || t.Audio.Frames == 0))
            throw new InvalidDataException("Invalid decoded audio.");
        initial = session with { Tracks = session.Tracks.ToList() };
        StereoMonitor = stereoMonitor;
        var speakers = SpatialMixer.Speakers(session.Layout);
        Channels = stereoMonitor ? 2 : speakers.Length;
        TotalFrames = tracks.Max(t => (long)Math.Round(t.Settings.StartSeconds * SampleRate)
            + (long)Math.Ceiling(t.Audio.Duration * SampleRate));
        if (TotalFrames <= 0 || TotalFrames > 3600L * SampleRate)
            throw new InvalidDataException("Render duration must be between zero and one hour.");
        RampFrames = Math.Max(1, (int)Math.Round(SampleRate * RampSeconds));
        lfeChannel = Array.FindIndex(speakers, s => s.IsLfe);
        states = tracks.Select(t => new TrackState(t, SampleRate, speakers.Length)).ToArray();
        mix = new double[speakers.Length];
        Meter = new LoudnessMeter(SampleRate, stereoMonitor ? SpatialMixer.Speakers(SpeakerLayout.Stereo) : speakers);
        parameters = active = Prepare(session);
        master = active.Master;
        for (int i = 0; i < states.Length; i++) states[i].Reset(active.Tracks[i]);
    }

    // The UI publishes immutable parameter blocks; the audio thread owns position, ramps and filter history.
    public void Update(SessionDocument session)
    {
        SessionStore.Validate(session);
        if (session.Layout != Layout || session.SampleRate != SampleRate || session.Tracks.Count != initial.Tracks.Count
            || session.Tracks.Where((t, i) => t.Id != initial.Tracks[i].Id || t.MediaPath != initial.Tracks[i].MediaPath
                || t.StartSeconds != initial.Tracks[i].StartSeconds).Any())
            throw new InvalidOperationException("Stop playback before changing media, offsets, layout or sample rate.");
        Volatile.Write(ref parameters, Prepare(session));
    }

    private Parameters Prepare(SessionDocument session)
    {
        bool solo = session.Tracks.Any(t => t.Solo);
        var tracks = session.Tracks.Select((t, i) =>
        {
            double gain = !t.Mute && (!solo || t.Solo) ? Gain(t.GainDb) : 0;
            var left = SpatialMixer.Pan(Layout, t.Azimuth - (states[i].IsStereo ? 30 : 0));
            var right = SpatialMixer.Pan(Layout, t.Azimuth + (states[i].IsStereo ? 30 : 0));
            for (int c = 0; c < left.Length; c++) { left[c] *= gain; right[c] *= gain; }
            return new TrackParameters(left, right, lfeChannel < 0 ? 0 : gain * Gain(t.LfeDb), gain > 0);
        }).ToArray();
        return new Parameters(tracks, Gain(session.MasterGainDb));
    }
    private static double Gain(double db) => db <= -60 ? 0 : Math.Pow(10, db / 20);

    // Returns frames, clears unused output, and preserves state across arbitrarily sized reads.
    // Call Read from one audio thread only. Update may run concurrently; a new block ramps in over RampFrames.
    public int Read(Span<double> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("Buffer must contain whole audio frames.", nameof(destination));
        destination.Clear();
        int frames = (int)Math.Min(destination.Length / Channels, TotalFrames - Position);
        var block = Volatile.Read(ref parameters);
        if (!ReferenceEquals(block, active)) Retarget(block);
        for (int f = 0; f < frames; f++)
        {
            if (f % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
            if (rampLeft > 0) Step();
            Array.Clear(mix);
            for (int i = 0; i < states.Length; i++) states[i].Add(Position, mix, lfeChannel);
            if (StereoMonitor) SpatialMixer.FoldStereo(mix, Layout, stereo);
            var output = StereoMonitor ? stereo : mix;
            for (int c = 0; c < Channels; c++)
            {
                double value = output[c] * master;
                if (!double.IsFinite(value)) throw new InvalidDataException("Mix contains non-finite samples.");
                destination[f * Channels + c] = value;
                Peak = Math.Max(Peak, Math.Abs(value));
            }
            Position++;
        }
        Meter.Process(destination[..(frames * Channels)]);
        return frames;
    }

    private void Retarget(Parameters block)
    {
        active = block;
        rampLeft = RampFrames;
        masterStep = (block.Master - master) / RampFrames;
        for (int i = 0; i < states.Length; i++) states[i].Retarget(block.Tracks[i], RampFrames);
    }
    private void Step()
    {
        bool last = --rampLeft == 0;
        master = last ? active.Master : master + masterStep;
        foreach (var state in states) state.Step(last);
    }

    private sealed record Parameters(TrackParameters[] Tracks, double Master);
    // Left/Right hold pan × track gain per output channel; zero when muted or not soloed.
    private sealed record TrackParameters(double[] Left, double[] Right, double Lfe, bool Audible);

    private sealed class TrackState
    {
        private readonly AudioBuffer audio;
        private readonly Resampler? resampler;
        private readonly double[] source;
        private readonly long offset;
        private readonly LowPass filter;
        private readonly double[] left, right, leftStep, rightStep;
        private TrackParameters target = null!;
        private double lfe, lfeStep;
        private bool silent;
        public bool IsStereo => audio.Channels == 2;
        public TrackState(LoadedTrack track, int rate, int channels)
        {
            audio = track.Audio;
            resampler = audio.SampleRate == rate ? null : new Resampler(audio.SampleRate, rate);
            source = new double[audio.Channels];
            offset = (long)Math.Round(track.Settings.StartSeconds * rate);
            filter = new LowPass(rate);
            left = new double[channels]; right = new double[channels];
            leftStep = new double[channels]; rightStep = new double[channels];
        }
        public void Reset(TrackParameters p)
        {
            target = p;
            p.Left.CopyTo(left, 0); p.Right.CopyTo(right, 0);
            lfe = p.Lfe; silent = !p.Audible;
        }
        public void Retarget(TrackParameters p, int frames)
        {
            target = p;
            for (int c = 0; c < left.Length; c++)
            {
                leftStep[c] = (p.Left[c] - left[c]) / frames;
                rightStep[c] = (p.Right[c] - right[c]) / frames;
            }
            lfeStep = (p.Lfe - lfe) / frames;
            silent = false;
        }
        public void Step(bool last)
        {
            if (last) { Reset(target); return; }
            for (int c = 0; c < left.Length; c++) { left[c] += leftStep[c]; right[c] += rightStep[c]; }
            lfe += lfeStep;
        }
        public void Add(long frame, double[] output, int lfeChannel)
        {
            long local = frame - offset;
            if (local < 0) return;
            double a, b;
            if (resampler is null) { a = audio.At(local, 0); b = IsStereo ? audio.At(local, 1) : 0; }
            else
            {
                resampler.Read(audio, local * resampler.Ratio, source);
                a = source[0]; b = IsStereo ? source[1] : 0;
            }
            // The low-pass history runs even while muted so a live LFE or unmute has no filter start-up transient.
            double bass = filter.Process(IsStereo ? (a + b) * .5 : a);
            if (silent) return;
            for (int c = 0; c < output.Length; c++) output[c] += a * left[c] + b * right[c];
            if (lfeChannel >= 0) output[lfeChannel] += bass * lfe;
        }
    }

    private sealed class LowPass
    {
        private readonly double b0, b1, b2, a1, a2;
        private double z1, z2;
        public LowPass(int sampleRate)
        {
            double w = 2 * Math.PI * 120 / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / Math.Sqrt(2), a0 = 1 + alpha;
            b0 = (1 - cos) / 2 / a0; b1 = (1 - cos) / a0; b2 = b0;
            a1 = -2 * cos / a0; a2 = (1 - alpha) / a0;
        }
        public double Process(double input)
        {
            double value = b0 * input + z1;
            z1 = b1 * input - a1 * value + z2;
            z2 = b2 * input - a2 * value;
            return value;
        }
    }
}
