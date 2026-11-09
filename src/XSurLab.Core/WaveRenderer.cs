using System.Text;

namespace XSurLab.Core;

public sealed record LoadedTrack(TrackSettings Settings, AudioBuffer Audio);
public sealed record RenderResult(long Frames, int Channels, int SampleRate, double Peak)
{
    public double PeakDb => Peak > 0 ? 20 * Math.Log10(Peak) : double.NegativeInfinity;
    public double Duration => (double)Frames / SampleRate;
}

public static class WaveRenderer
{
    public static RenderResult Render(string destination, SessionDocument session, IReadOnlyList<LoadedTrack> tracks,
        bool stereoMonitor = false, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        SessionStore.Validate(session);
        if (!session.Tracks.SequenceEqual(tracks.Select(t => t.Settings)))
            throw new InvalidDataException("Loaded media does not match the session.");
        destination = Path.GetFullPath(destination);
        if (tracks.Any(t => string.Equals(Path.GetFullPath(t.Settings.MediaPath), destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a different filename; source media cannot be overwritten.");
        bool anySolo = tracks.Any(t => t.Settings.Solo);
        var active = tracks.Where(t => !t.Settings.Mute && (!anySolo || t.Settings.Solo)).ToArray();
        if (active.Length == 0) throw new InvalidOperationException("No audible tracks. Import audio or check mute/solo.");
        var rate = session.SampleRate;
        long frames = tracks.Max(t => (long)Math.Round(t.Settings.StartSeconds * rate) + (long)Math.Ceiling(t.Audio.Duration * rate));
        if (frames <= 0 || frames > 3600L * rate) throw new InvalidDataException("Render duration must be between zero and one hour.");
        int channels = stereoMonitor ? 2 : SpatialMixer.Speakers(session.Layout).Length;
        int bits = stereoMonitor ? 16 : 24;
        long bytes = checked(frames * channels * (bits / 8));
        if (bytes > uint.MaxValue - 60) throw new InvalidDataException("Output exceeds the RIFF 4 GiB limit. Shorten the session.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            double peak = 0;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                WriteHeader(writer, rate, channels, bits, (uint)bytes, stereoMonitor ? 3u : SpatialMixer.ChannelMask(session.Layout));
                var states = active.Select(t => new TrackState(t, session.Layout, rate)).ToArray();
                var mix = new double[SpatialMixer.Speakers(session.Layout).Length];
                var stereo = new double[2];
                double master = session.MasterGainDb <= -60 ? 0 : Math.Pow(10, session.MasterGainDb / 20);
                for (long frame = 0; frame < frames; frame++)
                {
                    if (frame % 4096 == 0) { cancellationToken.ThrowIfCancellationRequested(); progress?.Report((double)frame / frames); }
                    Array.Clear(mix);
                    foreach (var state in states) state.Add(frame, mix);
                    if (stereoMonitor) SpatialMixer.FoldStereo(mix, session.Layout, stereo);
                    var output = stereoMonitor ? stereo : mix;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        double value = output[channel] * master;
                        peak = Math.Max(peak, Math.Abs(value));
                        if (bits == 16) writer.Write((short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue));
                        else
                        {
                            int pcm = (int)Math.Clamp(Math.Round(value * 8388608), -8388608, 8388607);
                            writer.Write((byte)pcm); writer.Write((byte)(pcm >> 8)); writer.Write((byte)(pcm >> 16));
                        }
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(peak) || peak > 1)
                throw new InvalidOperationException($"Render peaks at {20 * Math.Log10(peak):F1} dBFS. Lower track or master gain and retry. No output was replaced.");
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return new RenderResult(frames, channels, rate, peak);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteHeader(BinaryWriter writer, int rate, int channels, int bits, uint bytes, uint mask)
    {
        bool extensible = channels > 2;
        void Tag(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        Tag("RIFF"); writer.Write(bytes + (extensible ? 60u : 36u)); Tag("WAVE"); Tag("fmt ");
        writer.Write(extensible ? 40u : 16u); writer.Write((ushort)(extensible ? 0xfffe : 1));
        writer.Write((ushort)channels); writer.Write(rate); writer.Write(rate * channels * (bits / 8));
        writer.Write((ushort)(channels * (bits / 8))); writer.Write((ushort)bits);
        if (extensible)
        {
            writer.Write((ushort)22); writer.Write((ushort)bits); writer.Write(mask);
            writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        }
        Tag("data"); writer.Write(bytes);
    }

    private sealed class TrackState
    {
        private readonly LoadedTrack track;
        private readonly double[] left, right;
        private readonly double gain, lfe, ratio;
        private readonly long offset;
        private readonly LowPass filter;

        public TrackState(LoadedTrack track, SpeakerLayout layout, int rate)
        {
            this.track = track;
            bool stereo = track.Audio.Channels == 2;
            left = SpatialMixer.Pan(layout, track.Settings.Azimuth - (stereo ? 30 : 0));
            right = SpatialMixer.Pan(layout, track.Settings.Azimuth + (stereo ? 30 : 0));
            gain = track.Settings.GainDb <= -60 ? 0 : Math.Pow(10, track.Settings.GainDb / 20);
            lfe = track.Settings.LfeDb <= -60 || layout == SpeakerLayout.Stereo ? 0 : Math.Pow(10, track.Settings.LfeDb / 20);
            ratio = (double)track.Audio.SampleRate / rate;
            offset = (long)Math.Round(track.Settings.StartSeconds * rate);
            filter = new LowPass(rate);
        }

        public void Add(long frame, double[] output)
        {
            double source = (frame - offset) * ratio;
            if (source < 0) return;
            double a = track.Audio.Sample(source, 0) * gain;
            bool stereo = track.Audio.Channels == 2;
            double b = stereo ? track.Audio.Sample(source, 1) * gain : 0;
            for (int c = 0; c < output.Length; c++) output[c] += a * left[c] + b * right[c];
            if (lfe > 0) output[3] += filter.Process(stereo ? (a + b) * .5 : a) * lfe;
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
