using System.Text;

namespace XSurLab.Core;

public sealed record LoadedTrack(TrackSettings Settings, AudioBuffer Audio);
public sealed record RenderResult(long Frames, int Channels, int SampleRate, double Peak, LoudnessReport Loudness)
{
    public double PeakDb => Peak > 0 ? 20 * Math.Log10(Peak) : double.NegativeInfinity;
    public double Duration => (double)Frames / SampleRate;
}

public static class WaveRenderer
{
    public static RenderResult Render(string destination, SessionDocument session, IReadOnlyList<LoadedTrack> tracks,
        bool stereoMonitor = false, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var mixer = new StreamingMixer(session, tracks, stereoMonitor);
        destination = Path.GetFullPath(destination);
        if (tracks.Any(t => string.Equals(Path.GetFullPath(t.Settings.MediaPath), destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a different filename; source media cannot be overwritten.");
        int rate = mixer.SampleRate, channels = mixer.Channels;
        long frames = mixer.TotalFrames;
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
                var buffer = new double[1024 * channels];
                while (mixer.Position < mixer.TotalFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report((double)mixer.Position / frames);
                    int count = mixer.Read(buffer, cancellationToken) * channels;
                    for (int sample = 0; sample < count; sample++)
                    {
                        double value = buffer[sample];
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
            return new RenderResult(frames, channels, rate, peak, mixer.Meter.Report());
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

}

