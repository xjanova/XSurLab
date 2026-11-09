namespace XSurLab.Core;

public sealed class StreamingMixer
{
    private readonly SessionDocument initial;
    private readonly TrackState[] states;
    private readonly double[] mix;
    private readonly double[] stereo = new double[2];
    private Parameters parameters;
    public int SampleRate => initial.SampleRate;
    public SpeakerLayout Layout => initial.Layout;
    public bool StereoMonitor { get; }
    public int Channels { get; }
    public long TotalFrames { get; }
    public long Position { get; private set; }
    public double Peak { get; private set; }

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
        Channels = stereoMonitor ? 2 : SpatialMixer.Speakers(session.Layout).Length;
        TotalFrames = tracks.Max(t => (long)Math.Round(t.Settings.StartSeconds * SampleRate)
            + (long)Math.Ceiling(t.Audio.Duration * SampleRate));
        if (TotalFrames <= 0 || TotalFrames > 3600L * SampleRate)
            throw new InvalidDataException("Render duration must be between zero and one hour.");
        states = tracks.Select(t => new TrackState(t, SampleRate)).ToArray();
        mix = new double[SpatialMixer.Speakers(Layout).Length];
        parameters = Prepare(session);
    }

    // The UI publishes immutable parameter blocks; the audio thread owns position and filter history.
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
        var tracks = session.Tracks.Select((t, i) => new TrackParameters(
            SpatialMixer.Pan(Layout, t.Azimuth - (states[i].IsStereo ? 30 : 0)),
            SpatialMixer.Pan(Layout, t.Azimuth + (states[i].IsStereo ? 30 : 0)),
            Gain(t.GainDb), Layout == SpeakerLayout.Stereo ? 0 : Gain(t.LfeDb), !t.Mute && (!solo || t.Solo))).ToArray();
        return new Parameters(tracks, Gain(session.MasterGainDb));
    }
    private static double Gain(double db) => db <= -60 ? 0 : Math.Pow(10, db / 20);

    // Returns frames, clears unused output, and preserves state across arbitrarily sized reads.
    // Call Read from one audio thread only. Update may run concurrently.
    public int Read(Span<double> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("Buffer must contain whole audio frames.", nameof(destination));
        destination.Clear();
        int frames = (int)Math.Min(destination.Length / Channels, TotalFrames - Position);
        var block = Volatile.Read(ref parameters);
        for (int f = 0; f < frames; f++)
        {
            if (f % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(mix);
            for (int i = 0; i < states.Length; i++) states[i].Add(Position, block.Tracks[i], mix);
            if (StereoMonitor) SpatialMixer.FoldStereo(mix, Layout, stereo);
            var output = StereoMonitor ? stereo : mix;
            for (int c = 0; c < Channels; c++)
            {
                double value = output[c] * block.Master;
                if (!double.IsFinite(value)) throw new InvalidDataException("Mix contains non-finite samples.");
                destination[f * Channels + c] = value;
                Peak = Math.Max(Peak, Math.Abs(value));
            }
            Position++;
        }
        return frames;
    }

    private sealed record Parameters(TrackParameters[] Tracks, double Master);
    private sealed record TrackParameters(double[] Left, double[] Right, double Gain, double Lfe, bool Audible);

    private sealed class TrackState
    {
        private readonly AudioBuffer audio;
        private readonly double ratio;
        private readonly long offset;
        private readonly LowPass filter;
        public bool IsStereo => audio.Channels == 2;
        public TrackState(LoadedTrack track, int rate)
        {
            audio = track.Audio;
            ratio = (double)audio.SampleRate / rate;
            offset = (long)Math.Round(track.Settings.StartSeconds * rate);
            filter = new LowPass(rate);
        }
        public void Add(long frame, TrackParameters p, double[] output)
        {
            double source = (frame - offset) * ratio;
            if (source < 0) return;
            double a = audio.Sample(source, 0) * p.Gain;
            double b = IsStereo ? audio.Sample(source, 1) * p.Gain : 0;
            double bass = filter.Process(IsStereo ? (a + b) * .5 : a);
            if (!p.Audible) return;
            for (int c = 0; c < output.Length; c++) output[c] += a * p.Left[c] + b * p.Right[c];
            if (p.Lfe > 0) output[3] += bass * p.Lfe;
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
