using System.Text;
using XSurLab.Core;

var root = Path.Combine(Path.GetTempPath(), "XSurLab-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failures = 0, count = 0;
try
{
    Test("PCM16/24/32 and float32 imports preserve signed samples", () =>
    {
        foreach (var (format, bits) in new[] { (1, 16), (1, 24), (1, 32), (3, 32) })
        {
            var path = Fixture($"source-{format}-{bits}.wav", 2, bits, format, 4, (f, c) => c == 0 ? -.5 : .25);
            var audio = WaveReader.Read(path);
            Equal(audio.Frames, 4); Equal(audio.Channels, 2);
            Near(audio.Samples.Span[0], -.5, .00004); Near(audio.Samples.Span[1], .25, .00004);
        }
    });
    Test("Odd-sized unknown chunks and extensible stereo import", () =>
    {
        var source = Fixture("basic.wav", 2, 24, 1, 4, (_, _) => .125);
        var original = File.ReadAllBytes(source);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(original.AsSpan(0, 12));
        writer.Write(Encoding.ASCII.GetBytes("JUNK")); writer.Write(1u); writer.Write((byte)7); writer.Write((byte)0);
        writer.Write(original.AsSpan(12)); memory.Position = 4; writer.Write((uint)memory.Length - 8);
        var path = Path.Combine(root, "junk.wav"); File.WriteAllBytes(path, memory.ToArray());
        Near(WaveReader.Read(path).Samples.Span[0], .125);
        var ext = Path.Combine(root, "ext.wav");
        using var extMemory = new MemoryStream(); using var extWriter = new BinaryWriter(extMemory);
        extWriter.Write(original.AsSpan(0, 12)); extWriter.Write(Encoding.ASCII.GetBytes("fmt ")); extWriter.Write(40u);
        extWriter.Write((ushort)0xfffe); extWriter.Write(original.AsSpan(22, 14));
        extWriter.Write((ushort)22); extWriter.Write((ushort)24); extWriter.Write(3u);
        extWriter.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        extWriter.Write(original.AsSpan(36)); extMemory.Position = 4; extWriter.Write((uint)extMemory.Length - 8);
        File.WriteAllBytes(ext, extMemory.ToArray());
        Near(WaveReader.Read(ext).Samples.Span[0], .125);
    });
    Test("Malformed, compressed, multichannel and non-finite audio is rejected", () =>
    {
        var valid = Fixture("valid.wav", 1, 16, 1, 8, (_, _) => .1);
        var bytes = File.ReadAllBytes(valid);
        var truncated = Path.Combine(root, "truncated.wav"); File.WriteAllBytes(truncated, bytes[..^2]);
        Throws<InvalidDataException>(() => WaveReader.Read(truncated));
        var compressed = bytes.ToArray(); compressed[20] = 6;
        var bad = Path.Combine(root, "bad.wav"); File.WriteAllBytes(bad, compressed);
        Throws<InvalidDataException>(() => WaveReader.Read(bad));
        var multichannel = Fixture("multichannel.wav", 6, 16, 1, 8, (_, _) => .1);
        Throws<InvalidDataException>(() => WaveReader.Read(multichannel));
        var nan = Fixture("nan.wav", 1, 32, 3, 8, (_, _) => double.NaN);
        Throws<InvalidDataException>(() => WaveReader.Read(nan));
    });
    Test("Circular panning preserves power, reaches every speaker, excludes LFE", () =>
    {
        foreach (var layout in new[] { SpeakerLayout.Surround51, SpeakerLayout.Surround71 })
        {
            var speakers = SpatialMixer.Speakers(layout);
            foreach (var (speaker, index) in speakers.Select((s, i) => (s, i)).Where(x => !x.s.IsLfe))
                Near(SpatialMixer.Pan(layout, speaker.Azimuth)[index], 1);
            for (int angle = -180; angle <= 180; angle++)
            {
                var pan = SpatialMixer.Pan(layout, angle);
                Near(pan.Sum(g => g * g), 1); Near(pan[3], 0);
            }
            var a = SpatialMixer.Pan(layout, -180);
            var b = SpatialMixer.Pan(layout, 180);
            for (int c = 0; c < a.Length; c++) Near(a[c], b[c]);
        }
    });
    Test("5.1 / 7.1 exports carry the mask, byte count and center channel in order", () =>
    {
        var media = WaveReader.Read(Fixture("center.wav", 1, 16, 1, 48, (_, _) => .25));
        foreach (var (layout, channels, mask) in new[] { (SpeakerLayout.Surround51, 6, 0x60fu), (SpeakerLayout.Surround71, 8, 0x63fu) })
        {
            var track = Track("center.wav");
            var path = Path.Combine(root, $"{channels}ch.wav");
            var result = WaveRenderer.Render(path, Session(layout, track), [new(track, media)]);
            var bytes = File.ReadAllBytes(path);
            Equal(BitConverter.ToUInt16(bytes, 20), (ushort)0xfffe);
            Equal(BitConverter.ToUInt16(bytes, 22), (ushort)channels);
            Equal(BitConverter.ToUInt16(bytes, 34), (ushort)24);
            Equal(BitConverter.ToUInt32(bytes, 40), mask);
            Equal(BitConverter.ToUInt32(bytes, 64), (uint)(48 * channels * 3));
            Equal(bytes.Length, 68 + 48 * channels * 3);
            Equal(result.Frames, 48L);
            for (int c = 0; c < channels; c++) Near(Sample24(bytes, 0, c, channels), c == 2 ? .25 : 0);
        }
    });
    Test("Stereo sources keep L/R separation at default azimuth", () =>
    {
        var media = WaveReader.Read(Fixture("stereo.wav", 2, 16, 1, 48, (_, c) => c == 0 ? .25 : -.125));
        var track = Track("stereo.wav"); var path = Path.Combine(root, "stereo-out.wav");
        WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, track), [new(track, media)]);
        var bytes = File.ReadAllBytes(path);
        Near(Sample24(bytes, 0, 0, 6), .25); Near(Sample24(bytes, 0, 1, 6), -.125);
        Near(Sample24(bytes, 0, 2, 6), 0);
    });
    Test("Gain, solo, mute and track offsets affect rendered samples", () =>
    {
        var media = WaveReader.Read(Fixture("signal.wav", 1, 16, 1, 48, (_, _) => .25));
        var a = Track("signal.wav") with { Solo = true, GainDb = -6, StartSeconds = .001 };
        var b = Track("signal.wav") with { Azimuth = -30 };
        var session = Session(SpeakerLayout.Surround51, a, b);
        var path = Path.Combine(root, "solo.wav");
        WaveRenderer.Render(path, session, [new(a, media), new(b, media)]);
        var bytes = File.ReadAllBytes(path);
        Near(Sample24(bytes, 0, 2, 6), 0);
        Near(Sample24(bytes, 48, 2, 6), .25 * Math.Pow(10, -.3), .00001);
        Near(Sample24(bytes, 48, 0, 6), 0);
        var muted = a with { Mute = true };
        Throws<InvalidOperationException>(() => WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, muted, b), [new(muted, media), new(b, media)]));
    });
    Test("Stereo fold uses -3 dB center/surround and omits LFE", () =>
    {
        var source = new double[] { 1, 2, 3, 99, 4, 5, 6, 7 };
        var stereo = new double[2]; SpatialMixer.FoldStereo(source, SpeakerLayout.Surround71, stereo);
        Near(stereo[0], 1 + 13 * Math.Sqrt(.5)); Near(stereo[1], 2 + 15 * Math.Sqrt(.5));
    });
    Test("LFE send is explicit and low-pass attenuates high frequencies", () =>
    {
        double Rms(int hz, double send)
        {
            var path = Fixture($"tone-{hz}.wav", 1, 16, 1, 12000, (f, _) => .2 * Math.Sin(2 * Math.PI * hz * f / 48000));
            var audio = WaveReader.Read(path);
            var track = Track(Path.GetFileName(path)) with { LfeDb = send };
            var output = Path.Combine(root, $"lfe-{hz}-{send}.wav");
            WaveRenderer.Render(output, Session(SpeakerLayout.Surround51, track), [new(track, audio)]);
            var bytes = File.ReadAllBytes(output);
            return Math.Sqrt(Enumerable.Range(4800, 7200).Average(f => Math.Pow(Sample24(bytes, f, 3, 6), 2)));
        }
        Near(Rms(60, -60), 0);
        Check(Rms(60, 0) > Rms(6000, 0) * 30, "LFE high-frequency attenuation");
    });
    Test("Mixed-rate media renders expected duration and monitor header", () =>
    {
        var media = new AudioBuffer(24000, 1, Enumerable.Repeat(.25f, 240).ToArray());
        var track = Track("rate.wav");
        var path = Path.Combine(root, "rate-output.wav");
        var result = WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, track), [new(track, media)], true);
        Equal(result.Frames, 480L);
        var audio = WaveReader.Read(path);
        Equal(audio.SampleRate, 48000); Equal(audio.Channels, 2); Near(audio.Duration, .01);
        Near(audio.Samples.Span[0], .25 * Math.Sqrt(.5), .00004);
    });
    Test("Clipping, cancellation and source overwrite preserve destination and clean scratch", () =>
    {
        var media = new AudioBuffer(48000, 1, Enumerable.Repeat(.9f, 48).ToArray());
        var track = Track("original.wav") with { GainDb = 12 };
        var path = Path.Combine(root, "preserve.wav"); File.WriteAllText(path, "original content");
        Throws<InvalidOperationException>(() => WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, track), [new(track, media)]));
        Equal(File.ReadAllText(path), "original content");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, track), [new(track, media)], cancellationToken: cancel.Token));
        Equal(File.ReadAllText(path), "original content");
        Throws<IOException>(() => WaveRenderer.Render(track.MediaPath, Session(SpeakerLayout.Surround51, track), [new(track, media)]));
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "No temporary files remain");
    });
    Test("Decoder cancellation stops import", () =>
    {
        var path = Fixture("cancel-import.wav", 1, 16, 1, 48, (_, _) => .1);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => WaveReader.Read(path, cancel.Token));
    });
    Test("Session save refuses source audio and leaves bytes intact", () =>
    {
        var path = Fixture("do-not-overwrite.wav", 1, 16, 1, 48, (_, _) => .1);
        var original = File.ReadAllBytes(path);
        var track = Track("do-not-overwrite.wav");
        Throws<IOException>(() => SessionStore.Save(path, Session(SpeakerLayout.Stereo, track)));
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Source audio must survive session save");
    });
    Test("Session JSON round-trips paths/settings; invalid versions and ranges fail", () =>
    {
        var track = Track("session-media.wav") with { StartSeconds = 2.5, Azimuth = -110, Solo = true };
        var session = Session(SpeakerLayout.Surround71, track);
        var path = Path.Combine(root, "project.xsurlab.json");
        SessionStore.Save(path, session);
        Check(!File.ReadAllText(path).Contains(root.Replace("\\", "\\\\")), "Media path should be relative");
        var loaded = SessionStore.Load(path);
        Equal(loaded.Tracks.Single(), track); Equal(loaded.Layout, session.Layout);
        Throws<InvalidDataException>(() => SessionStore.Save(path, session with { Version = 99 }));
        Throws<InvalidDataException>(() => SessionStore.Validate(session with { MasterGainDb = double.NaN }));
        Throws<InvalidDataException>(() => SessionStore.Validate(session with { Tracks = [track with { StartSeconds = -1 }] }));
        Throws<InvalidDataException>(() => SessionStore.Validate(session with { Tracks = [track, track] }));
    });
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"{count - failures}/{count} checks passed.");
Environment.ExitCode = failures == 0 ? 0 : 1;

void Test(string name, Action action)
{
    count++;
    try { action(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Equal<T>(T actual, T expected) => Check(EqualityComparer<T>.Default.Equals(actual, expected), $"Expected {expected}, got {actual}.");
void Near(double actual, double expected, double tolerance = .000001) => Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}.");
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
TrackSettings Track(string name) => new() { MediaPath = Path.Combine(root, name), Name = name };
SessionDocument Session(SpeakerLayout layout, params TrackSettings[] tracks) => new() { Layout = layout, MasterGainDb = 0, Tracks = tracks.ToList() };
double Sample24(byte[] bytes, int frame, int channel, int channels)
{
    int i = 68 + (frame * channels + channel) * 3;
    int sample = bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16;
    return (sample << 8 >> 8) / 8388608d;
}
string Fixture(string name, int channels, int bits, int format, int frames, Func<int, int, double> value)
{
    var path = Path.Combine(root, name);
    using var writer = new BinaryWriter(File.Create(path));
    int size = frames * channels * bits / 8;
    void Tag(string text) => writer.Write(Encoding.ASCII.GetBytes(text));
    Tag("RIFF"); writer.Write(36 + size + (size & 1)); Tag("WAVE"); Tag("fmt "); writer.Write(16);
    writer.Write((ushort)format); writer.Write((ushort)channels); writer.Write(48000);
    writer.Write(48000 * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
    Tag("data"); writer.Write(size);
    for (int f = 0; f < frames; f++)
        for (int c = 0; c < channels; c++)
        {
            double sample = value(f, c);
            if (format == 3) writer.Write((float)sample);
            else if (bits == 16) writer.Write((short)Math.Round(sample * 32768));
            else if (bits == 24)
            {
                int pcm = (int)Math.Round(sample * 8388608);
                writer.Write((byte)pcm); writer.Write((byte)(pcm >> 8)); writer.Write((byte)(pcm >> 16));
            }
            else writer.Write((int)Math.Round(sample * 2147483648));
        }
    if ((size & 1) != 0) writer.Write((byte)0);
    return path;
}

