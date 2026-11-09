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
        if (tracks.Any(t => t.Audio.SampleRate <= 0 || t.Audio.Frames == 0 || !SpatialMixer.IsValidMask(t.Audio.ChannelMask, t.Audio.Channels)))
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
        states = tracks.Select(t => new TrackState(t, SampleRate, Layout, speakers.Length)).ToArray();
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
            var state = states[i];
            double gain = !t.Mute && (!solo || t.Solo) ? Gain(t.GainDb) : 0;
            double[][] routes = state.Bed ?? (state.SourceChannels == 1
                ? [SpatialMixer.Pan(Layout, t.Azimuth, t.Elevation, t.Spread)]
                : [SpatialMixer.Pan(Layout, t.Azimuth - 30, t.Elevation, t.Spread), SpatialMixer.Pan(Layout, t.Azimuth + 30, t.Elevation, t.Spread)]);
            routes = routes.Select(route => route.Select(g => g * gain).ToArray()).ToArray();
            double lfe = lfeChannel < 0 || state.Bed is not null ? 0 : gain * Gain(t.LfeDb);
            return new TrackParameters(routes, lfe, gain > 0);
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
    // Routes[source channel][output channel] holds pan or bed routing × track gain; all zero when muted or not soloed.
    private sealed record TrackParameters(double[][] Routes, double Lfe, bool Audible);

    private sealed class TrackState
    {
        private readonly AudioReader audio;
        private readonly Resampler? resampler;
        private readonly double[] source;
        private readonly long offset;
        private readonly LowPass filter;
        private readonly double[][] current, steps;
        private TrackParameters target = null!;
        private double lfe, lfeStep;
        private bool silent;
        public int SourceChannels => source.Length;
        /// <summary>Fixed routing of each channel of multichannel media; null for panned mono and stereo sources.</summary>
        public double[][]? Bed { get; }

        public TrackState(LoadedTrack track, int rate, SpeakerLayout layout, int outputs)
        {
            var media = track.Audio;
            audio = media.OpenReader();
            resampler = media.SampleRate == rate ? null : new Resampler(media.SampleRate, rate);
            source = new double[media.Channels];
            offset = (long)Math.Round(track.Settings.StartSeconds * rate);
            filter = new LowPass(rate);
            if (media.Channels > 2) Bed = SpatialMixer.Bits(media.ChannelMask).Select(bit => SpatialMixer.Route(layout, bit)).ToArray();
            current = Enumerable.Range(0, media.Channels).Select(_ => new double[outputs]).ToArray();
            steps = Enumerable.Range(0, media.Channels).Select(_ => new double[outputs]).ToArray();
        }
        public void Reset(TrackParameters p)
        {
            target = p;
            for (int s = 0; s < current.Length; s++) p.Routes[s].CopyTo(current[s], 0);
            lfe = p.Lfe; silent = !p.Audible;
        }
        public void Retarget(TrackParameters p, int frames)
        {
            target = p;
            for (int s = 0; s < current.Length; s++)
                for (int c = 0; c < current[s].Length; c++) steps[s][c] = (p.Routes[s][c] - current[s][c]) / frames;
            lfeStep = (p.Lfe - lfe) / frames;
            silent = false;
        }
        public void Step(bool last)
        {
            if (last) { Reset(target); return; }
            for (int s = 0; s < current.Length; s++)
                for (int c = 0; c < current[s].Length; c++) current[s][c] += steps[s][c];
            lfe += lfeStep;
        }
        public void Add(long frame, double[] output, int lfeChannel)
        {
            long local = frame - offset;
            if (local < 0) return;
            if (resampler is null) for (int s = 0; s < source.Length; s++) source[s] = audio.At(local, s);
            else resampler.Read(audio, local * resampler.Ratio, source);
            // The low-pass history runs even while muted so a live LFE or unmute has no filter start-up transient.
            double bass = Bed is null ? filter.Process(source.Length == 2 ? (source[0] + source[1]) * .5 : source[0]) : 0;
            if (silent) return;
            for (int s = 0; s < source.Length; s++)
            {
                double x = source[s];
                if (x == 0) continue;
                var route = current[s];
                for (int c = 0; c < output.Length; c++) output[c] += x * route[c];
            }
            if (lfeChannel >= 0 && lfe != 0) output[lfeChannel] += bass * lfe;
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
