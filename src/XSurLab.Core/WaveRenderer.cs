namespace XSurLab.Core;

public sealed record LoadedTrack(TrackSettings Settings, AudioBuffer Audio);
public sealed record RenderResult(long Frames, int Channels, int SampleRate, double Peak, LoudnessReport Loudness)
{
    public double PeakDb => Peak > 0 ? 20 * Math.Log10(Peak) : double.NegativeInfinity;
    public double Duration => (double)Frames / SampleRate;
}

public static class WaveRenderer
{
    public const string Version = "0.3";

    public static RenderResult Render(string destination, SessionDocument session, IReadOnlyList<LoadedTrack> tracks,
        ExportOptions? options = null, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        options ??= new();
        var mixer = new StreamingMixer(session, tracks, options.StereoFoldDown);
        destination = Path.GetFullPath(destination);
        if (tracks.Any(t => string.Equals(Path.GetFullPath(t.Settings.MediaPath), destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a different filename; source media cannot be overwritten.");
        uint mask = options.StereoFoldDown ? 3u : SpatialMixer.ChannelMask(session.Layout);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            LoudnessReport loudness;
            using (var writer = new WaveWriter(temporary, mixer.SampleRate, mixer.Channels, mask, mixer.TotalFrames, options,
                Describe(session, options, mixer.Channels, mask)))
            {
                var buffer = new double[1024 * mixer.Channels];
                while (mixer.Position < mixer.TotalFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report((double)mixer.Position / mixer.TotalFrames);
                    int frames = mixer.Read(buffer, cancellationToken);
                    writer.Write(buffer.AsSpan(0, frames * mixer.Channels));
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (options.Format != SampleFormat.Float32 && mixer.Peak > 1)
                    throw new InvalidOperationException($"Render peaks at {20 * Math.Log10(mixer.Peak):F1} dBFS. Lower track or master gain, or export 32-bit float. No output was replaced.");
                loudness = mixer.Meter.Report();
                writer.Finish(loudness);
            }
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return new RenderResult(mixer.TotalFrames, mixer.Channels, mixer.SampleRate, mixer.Peak, loudness);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static BroadcastInfo Describe(SessionDocument session, ExportOptions options, int channels, uint mask)
    {
        string mode = channels switch { 1 => "mono", 2 => "stereo", _ => "multichannel" };
        string coding = options.Format == SampleFormat.Float32 ? "A=PCM,F={0},W=32,M={1},T=IEEE float;XSurLab {2}"
            : "A=PCM,F={0},W={3},M={1},T=XSurLab {2}" + (options.Dither ? ";TPDF dither" : "");
        var history = string.Format(System.Globalization.CultureInfo.InvariantCulture, coding, session.SampleRate, mode, Version, options.BitsPerSample)
            + $";{channels} ch mask 0x{mask:X}\r\n";
        return new BroadcastInfo(string.IsNullOrWhiteSpace(options.Description) ? session.Name : options.Description!,
            "XSurLab " + Version, "XSL" + Guid.NewGuid().ToString("N")[..29], options.Created ?? DateTime.Now, history);
    }
}
