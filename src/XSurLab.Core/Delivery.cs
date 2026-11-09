using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XSurLab.Core;

public enum LoudnessTarget { None, EbuR128, AtscA85, Streaming }

public sealed record DeliveryOptions
{
    public ExportOptions Export { get; init; } = new();
    /// <summary>The interleaved mix, e.g. Film.wav.</summary>
    public bool Mix { get; init; } = true;
    /// <summary>One mono file per mix channel for encoders, e.g. Film.L.wav, Film.C.wav.</summary>
    public bool MultiMono { get; init; }
    /// <summary>One interleaved file per track group, e.g. Film_DX.wav. Stems ignore solo and match the mix length.</summary>
    public bool Stems { get; init; }
    public LoudnessTarget Target { get; init; }
    /// <summary>Replace files of an earlier delivery. Without it, any existing file stops the delivery before rendering.</summary>
    public bool Overwrite { get; init; }
}

public sealed record DeliveredFile(string Name, string Kind, int Channels, uint ChannelMask, string Speakers, long Frames, int SampleRate,
    int BitsPerSample, string Container, long Bytes, string Sha256, double IntegratedLufs, double RangeLu, double TruePeakDbtp,
    double SamplePeakDbfs, string[] SilentChannels);

public sealed record QcCheck(string Name, bool Passed, string Detail);

public sealed record DeliveryReport(string Tool, string Session, SpeakerLayout Layout, int SampleRate, string Format, DateTime Created,
    LoudnessTarget Target, IReadOnlyList<DeliveredFile> Files, IReadOnlyList<QcCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);
}

/// <summary>
/// Renders a delivery in one pass: the mix, its multi-mono split and the group stems, all the same length. While it
/// renders it checks that the stems sum to the mix; afterwards it hashes every file and writes a QC report
/// (name.qc.json and name.qc.txt) last, so a report means every audio file of the delivery is in place.
/// </summary>
public static class Delivery
{
    private const string Other = "Other";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Integrated target, tolerance in LU and maximum true peak of a delivery spec.</summary>
    public static (double Lufs, double Tolerance, double TruePeak)? Spec(LoudnessTarget target) => target switch
    {
        LoudnessTarget.EbuR128 => (-23, 1, -1),
        LoudnessTarget.AtscA85 => (-24, 2, -2),
        LoudnessTarget.Streaming => (-14, 1, -1),
        _ => null
    };

    public static string GroupOf(TrackSettings track) => track.Group.Length == 0 ? Other : track.Group;

    /// <summary>Every file a delivery would write, audio first and the two QC reports last.</summary>
    public static IReadOnlyList<string> PlannedFiles(string mixPath, SessionDocument session, DeliveryOptions options)
    {
        var (folder, name) = Split(mixPath);
        var files = new List<string>();
        if (options.Mix) files.Add(Path.Combine(folder, name + ".wav"));
        if (options.MultiMono) files.AddRange(Speakers(session, options).Select(s => Path.Combine(folder, $"{name}.{s.Name}.wav")));
        if (options.Stems) files.AddRange(session.Tracks.Select(GroupOf).Distinct().Select(g => Path.Combine(folder, $"{name}_{g}.wav")));
        files.Add(Path.Combine(folder, name + ".qc.json"));
        files.Add(Path.Combine(folder, name + ".qc.txt"));
        return files;
    }

    public static DeliveryReport Deliver(string mixPath, SessionDocument session, IReadOnlyList<LoadedTrack> tracks, DeliveryOptions options,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        if (!options.Mix && !options.MultiMono && !options.Stems) throw new InvalidOperationException("Choose the mix, multi-mono files or stems.");
        var planned = PlannedFiles(mixPath, session, options);
        if (planned.Distinct(StringComparer.OrdinalIgnoreCase).Count() != planned.Count)
            throw new InvalidOperationException("Two delivery files would share a name. Rename a group.");
        if (planned.Any(f => tracks.Any(t => string.Equals(Path.GetFullPath(t.Settings.MediaPath), f, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("Choose a different name; a delivery file would overwrite source media.");
        var existing = planned.Where(File.Exists).ToArray();
        if (existing.Length > 0 && !options.Overwrite)
            throw new IOException($"{existing.Length} delivery file(s) already exist, e.g. {Path.GetFileName(existing[0])}. Allow replacing or choose another name.");
        var export = options.Export;
        bool stereo = export.StereoFoldDown;
        var mix = new StreamingMixer(session, tracks, stereo);
        long frames = mix.TotalFrames;
        var speakers = Speakers(session, options);
        var (folder, name) = Split(mixPath);
        Directory.CreateDirectory(folder);

        // Stems ignore solo, which is a monitoring aid, and keep mute.
        var stems = new List<(string Group, StreamingMixer Mixer)>();
        var skipped = new List<string>();
        if (options.Stems)
            foreach (var group in session.Tracks.Select(GroupOf).Distinct())
            {
                var members = tracks.Where(t => GroupOf(t.Settings) == group).Select(t => t with { Settings = t.Settings with { Solo = false } }).ToList();
                var part = session with { Tracks = members.Select(t => t.Settings).ToList() };
                if (members.All(t => t.Settings.Mute)) { skipped.Add(group); continue; }
                stems.Add((group, new StreamingMixer(part, members, stereo, frames)));
            }

        var outputs = new List<Output>();
        try
        {
            ulong seed = export.DitherSeed;
            Output Open(string final, string kind, string label, int channels, uint mask, int source, int channel, LoudnessMeter? meter)
            {
                var file = new ExportOptions
                {
                    Format = export.Format, Dither = export.Dither, BroadcastWave = export.BroadcastWave, Rf64 = export.Rf64,
                    Created = export.Created, Description = $"{session.Name} · {label}",
                    // Each file gets its own dither sequence so multi-mono channels do not carry identical noise.
                    DitherSeed = seed + (ulong)outputs.Count * 0x9E3779B97F4A7C15UL
                };
                var temporary = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var output = new Output(final, temporary, kind, label, channels, mask, source, channel, meter, file);
                outputs.Add(output);
                output.Writer = new WaveWriter(temporary, mix.SampleRate, channels, mask, frames, file, WaveRenderer.Describe(session, file, channels, mask));
                return output;
            }
            uint mixMask = stereo ? 3u : SpatialMixer.ChannelMask(session.Layout);
            if (options.Mix) Open(Path.Combine(folder, name + ".wav"), "Mix", "mix", mix.Channels, mixMask, -1, -1, null);
            if (options.MultiMono)
                for (int c = 0; c < speakers.Length; c++)
                    Open(Path.Combine(folder, $"{name}.{speakers[c].Name}.wav"), "Multi-mono", speakers[c].Name, 1, 0, -1, c,
                        new LoudnessMeter(mix.SampleRate, [speakers[c]]));
            for (int s = 0; s < stems.Count; s++)
                Open(Path.Combine(folder, $"{name}_{stems[s].Group}.wav"), "Stem", stems[s].Group, mix.Channels, mixMask, s, -1, null);

            int block = 1024;
            var mixBuffer = new double[block * mix.Channels];
            var stemBuffers = stems.Select(_ => new double[block * mix.Channels]).ToArray();
            var mono = new double[block];
            double residual = 0;
            while (mix.Position < frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(.9 * mix.Position / frames);
                int count = mix.Read(mixBuffer, cancellationToken), samples = count * mix.Channels;
                for (int s = 0; s < stems.Count; s++)
                    if (stems[s].Mixer.Read(stemBuffers[s], cancellationToken) != count) throw new InvalidOperationException("Stem length drifted from the mix.");
                if (stems.Count > 0)
                    for (int i = 0; i < samples; i++)
                    {
                        double sum = 0;
                        for (int s = 0; s < stems.Count; s++) sum += stemBuffers[s][i];
                        residual = Math.Max(residual, Math.Abs(mixBuffer[i] - sum));
                    }
                foreach (var output in outputs)
                {
                    if (output.Channel >= 0)
                    {
                        for (int f = 0; f < count; f++) mono[f] = mixBuffer[f * mix.Channels + output.Channel];
                        output.Writer!.Write(mono.AsSpan(0, count));
                        output.Meter!.Process(mono.AsSpan(0, count));
                    }
                    else output.Writer!.Write((output.Source < 0 ? mixBuffer : stemBuffers[output.Source]).AsSpan(0, samples));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var peaks = new List<(string Label, double Peak)> { ("mix", mix.Peak) };
            peaks.AddRange(stems.Select(s => ($"stem {s.Group}", s.Mixer.Peak)));
            if (export.Format != SampleFormat.Float32 && peaks.FirstOrDefault(p => p.Peak > 1) is { Label: not null } clip)
                throw new InvalidOperationException($"The {clip.Label} peaks at {20 * Math.Log10(clip.Peak):F1} dBFS. Lower the gain or deliver 32-bit float. No file was replaced.");

            var files = new List<DeliveredFile>();
            for (int i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                var loudness = output.Meter?.Report() ?? (output.Source < 0 ? mix.Meter.Report() : stems[output.Source].Mixer.Meter.Report());
                output.Writer!.Finish(loudness);
                output.Writer.Dispose(); output.Writer = null;
                progress?.Report(.9 + .1 * i / outputs.Count);
                string hash;
                using (var stream = new FileStream(output.Temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                    hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                var bytes = new FileInfo(output.Temporary).Length;
                var names = output.Channel >= 0 ? [speakers[output.Channel]] : speakers;
                files.Add(new DeliveredFile(Path.GetFileName(output.Final), output.Kind, output.Channels, output.Mask,
                    string.Join(" ", names.Select(s => s.Name)), frames, mix.SampleRate, output.Options.BitsPerSample,
                    bytes - 8 > uint.MaxValue || output.Options.Rf64 ? "RF64" : "RIFF", bytes, hash, loudness.Integrated, loudness.Range,
                    loudness.TruePeakDb, loudness.SamplePeakDb,
                    loudness.ChannelPeaks.Select((p, c) => (p, c)).Where(x => x.p == 0).Select(x => names[x.c].Name).ToArray()));
            }
            foreach (var output in outputs) File.Move(output.Temporary, output.Final, overwrite: true);

            var checks = Checks(session, options, mix, files, stems.Count > 0 ? residual : null, skipped);
            var report = new DeliveryReport("XSurLab " + WaveRenderer.Version, session.Name, session.Layout, mix.SampleRate,
                $"{export.Format}{(export.Dither && export.Format != SampleFormat.Float32 ? " + TPDF dither" : "")}{(stereo ? " · stereo fold-down" : "")}",
                export.Created ?? DateTime.Now, options.Target, files, checks);
            File.WriteAllText(planned[^2], JsonSerializer.Serialize(report, Json));
            // The text report carries a BOM so Notepad, Excel and older shells read "·" and "±" as UTF-8.
            File.WriteAllText(planned[^1], Text(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            progress?.Report(1);
            return report;
        }
        finally
        {
            foreach (var output in outputs)
            {
                output.Writer?.Dispose();
                if (File.Exists(output.Temporary)) File.Delete(output.Temporary);
            }
        }
    }

    private static List<QcCheck> Checks(SessionDocument session, DeliveryOptions options, StreamingMixer mix, List<DeliveredFile> files,
        double? residual, List<string> skipped)
    {
        var checks = new List<QcCheck>();
        var program = mix.Meter.Report();
        var over = files.Where(f => f.SamplePeakDbfs > 0).Select(f => f.Name).ToArray();
        checks.Add(new("Sample peak at or below 0 dBFS", over.Length == 0, over.Length == 0 ? $"mix {Db(program.SamplePeakDb)} dBFS" : "over: " + string.Join(", ", over)));
        if (Spec(options.Target) is { } spec)
        {
            bool inRange = Math.Abs(program.Integrated - spec.Lufs) <= spec.Tolerance;
            checks.Add(new(FormattableString.Invariant($"Integrated loudness {spec.Lufs:0} ±{spec.Tolerance:0} LUFS"), inRange, $"{Db(program.Integrated)} LUFS"));
            checks.Add(new(FormattableString.Invariant($"True peak at or below {spec.TruePeak:0} dBTP"), program.TruePeakDb <= spec.TruePeak, $"{Db(program.TruePeakDb)} dBTP"));
        }
        var soloed = session.Tracks.Where(t => t.Solo).Select(t => t.Name).ToArray();
        checks.Add(new("No track is soloed", soloed.Length == 0, soloed.Length == 0 ? "the mix contains every unmuted track" : "the mix only contains: " + string.Join(", ", soloed)));
        if (residual is { } r)
            checks.Add(new("Stems sum to the mix", r <= 1e-6, $"largest difference {Db(r > 0 ? 20 * Math.Log10(r) : double.NegativeInfinity)} dBFS"
                + (skipped.Count > 0 ? $"; skipped fully muted: {string.Join(", ", skipped)}" : "")));
        return checks;
    }

    public static string Text(DeliveryReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{report.Tool} delivery QC · {report.Session}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{report.Created:yyyy-MM-dd HH:mm:ss} · {report.Layout} · {report.SampleRate} Hz · {report.Format} · target {report.Target}");
        text.AppendLine(report.Passed ? "RESULT  PASS" : "RESULT  FAIL");
        text.AppendLine();
        foreach (var check in report.Checks) text.AppendLine(CultureInfo.InvariantCulture, $"{(check.Passed ? "PASS" : "FAIL"),-5} {check.Name} · {check.Detail}");
        text.AppendLine();
        foreach (var f in report.Files)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{f.Name}  [{f.Kind}]  {f.Channels} ch {f.Speakers}  {f.BitsPerSample}-bit {f.Container}  {(double)f.Frames / f.SampleRate:0.000} s  {f.Bytes:N0} bytes");
            string silent = f.SilentChannels.Length > 0 ? " · silent: " + string.Join(" ", f.SilentChannels) : "";
            text.AppendLine(CultureInfo.InvariantCulture, $"      {Db(f.IntegratedLufs)} LUFS · LRA {Db(f.RangeLu)} LU · {Db(f.TruePeakDbtp)} dBTP · peak {Db(f.SamplePeakDbfs)} dBFS{silent}");
            text.AppendLine(CultureInfo.InvariantCulture, $"      SHA-256 {f.Sha256}");
        }
        return text.ToString();
    }

    private static string Db(double value) => double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : value < 0 ? "-inf" : "n/a";
    private static Speaker[] Speakers(SessionDocument session, DeliveryOptions options)
        => SpatialMixer.Speakers(options.Export.StereoFoldDown ? SpeakerLayout.Stereo : session.Layout);
    private static (string Folder, string Name) Split(string mixPath)
    {
        var full = Path.GetFullPath(mixPath);
        var name = Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Choose a file name for the mix.", nameof(mixPath));
        return (Path.GetDirectoryName(full)!, name);
    }

    private sealed record Output(string Final, string Temporary, string Kind, string Label, int Channels, uint Mask, int Source, int Channel,
        LoudnessMeter? Meter, ExportOptions Options)
    {
        public WaveWriter? Writer { get; set; }
    }
}
