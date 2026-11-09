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
            Near(audio[0, 0], -.5, .00004); Near(audio[0, 1], .25, .00004);
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
        Near(WaveReader.Read(path)[0, 0], .125);
        var ext = Path.Combine(root, "ext.wav");
        using var extMemory = new MemoryStream(); using var extWriter = new BinaryWriter(extMemory);
        extWriter.Write(original.AsSpan(0, 12)); extWriter.Write(Encoding.ASCII.GetBytes("fmt ")); extWriter.Write(40u);
        extWriter.Write((ushort)0xfffe); extWriter.Write(original.AsSpan(22, 14));
        extWriter.Write((ushort)22); extWriter.Write((ushort)24); extWriter.Write(3u);
        extWriter.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        extWriter.Write(original.AsSpan(36)); extMemory.Position = 4; extWriter.Write((uint)extMemory.Length - 8);
        File.WriteAllBytes(ext, extMemory.ToArray());
        Near(WaveReader.Read(ext)[0, 0], .125);
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
        var multichannel = Fixture("ambiguous-8ch.wav", 8, 16, 1, 8, (_, _) => .1);
        Throws<InvalidDataException>(() => WaveReader.Read(multichannel));
        var nan = Fixture("nan.wav", 1, 32, 3, 8, (_, _) => double.NaN);
        Throws<InvalidDataException>(() => WaveReader.Read(nan));
    });
    Test("Circular panning preserves power, reaches every speaker, excludes LFE", () =>
    {
        foreach (var layout in new[] { SpeakerLayout.Surround51, SpeakerLayout.Surround51Back, SpeakerLayout.Surround71 })
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
    Test("Height layouts pan in two layers; elevation and spread keep power", () =>
    {
        foreach (var layout in Enum.GetValues<SpeakerLayout>())
        {
            int lfe = Array.FindIndex(SpatialMixer.Speakers(layout), s => s.IsLfe);
            for (int angle = -180; angle <= 180; angle += 7)
                foreach (var elevation in new[] { 0, 10, 30, 45, 60, 90 })
                    foreach (var spread in new[] { 0, 30, 90, 200, 360 })
                    {
                        var pan = SpatialMixer.Pan(layout, angle, elevation, spread);
                        Near(pan.Sum(g => g * g), 1, 1e-9);
                        if (lfe >= 0) Equal(pan[lfe], 0d);
                    }
        }
        Equal(SpatialMixer.ChannelMask(SpeakerLayout.Surround514), 0x2d60fu); Equal(SpatialMixer.ChannelMask(SpeakerLayout.Surround714), 0x2d63fu);
        var speakers = SpatialMixer.Speakers(SpeakerLayout.Surround514);
        int Index(string name) => Array.FindIndex(speakers, s => s.Name == name);
        Near(SpatialMixer.Pan(SpeakerLayout.Surround514, 45, 45)[Index("Tfr")], 1);
        var overhead = SpatialMixer.Pan(SpeakerLayout.Surround514, 17, 90);
        foreach (var name in new[] { "Tfl", "Tfr", "Tbl", "Tbr" }) Near(overhead[Index(name)], .5);
        var low = SpatialMixer.Pan(SpeakerLayout.Surround514, 0, 20);
        double lifted = speakers.Select((s, i) => s.Elevation > 0 ? low[i] * low[i] : 0).Sum();
        Near(lifted, Math.Pow(Math.Sin(20 / 45d * Math.PI / 2), 2), 1e-9);
        Near(low[Index("Tfl")], low[Index("Tfr")]); Near(low[Index("C")] * low[Index("C")], 1 - lifted, 1e-9);
        var flat = SpatialMixer.Pan(SpeakerLayout.Surround51, 30, 60); var same = SpatialMixer.Pan(SpeakerLayout.Surround51, 30);
        for (int c = 0; c < flat.Length; c++) Near(flat[c], same[c]);
        var wide = SpatialMixer.Pan(SpeakerLayout.Surround51, 0, 0, 120);
        Near(wide[0], wide[1]); Near(wide[4], wide[5]);
        // Half of a ±60° arc lies outside ±30°, so L/R carry more than C and a little leaks to the surrounds.
        Check(wide[2] is > .3 and < 1 && wide[0] > wide[2] && wide[0] > wide[4] && wide[4] > 0, "A 120° spread centred ahead covers L/C/R");
        var all = SpatialMixer.Pan(SpeakerLayout.Surround71, 0, 0, 360); var turned = SpatialMixer.Pan(SpeakerLayout.Surround71, 2.5, 0, 360);
        double drift = all.Zip(turned, (a, b) => Math.Abs(a - b)).Max();
        Console.WriteLine($"  360° spread drift under a 2.5° turn {drift:F4}");
        Check(drift < .02, "A full spread must not depend on the azimuth");
        foreach (var s in SpatialMixer.Speakers(SpeakerLayout.Surround714))
            Equal(LoudnessMeter.Weight(s), s.IsLfe ? 0 : s.Elevation == 0 && Math.Abs(s.Azimuth) == 90 ? 1.41 : 1);
    });
    Test("7.1.4 renders heights in WAVE order, folds them to stereo and accepts 5.1.4 beds", () =>
    {
        var tone = new AudioBuffer(48000, 1, Enumerable.Repeat(.25f, 48).ToArray());
        var up = Track("up.wav") with { Azimuth = 45, Elevation = 45 };
        var path = Path.Combine(root, "714.wav");
        var result = WaveRenderer.Render(path, Session(SpeakerLayout.Surround714, up), [new(up, tone)]);
        var bytes = File.ReadAllBytes(path);
        Equal(result.Channels, 12); Equal(BitConverter.ToUInt32(bytes, Chunks(bytes)["fmt "].Offset + 20), 0x2d63fu);
        for (int c = 0; c < 12; c++) Near(Sample24(bytes, 10, c, 12), c == 9 ? .25 : 0);
        var folded = Path.Combine(root, "714-fold.wav");
        WaveRenderer.Render(folded, Session(SpeakerLayout.Surround714, up), [new(up, tone)], new() { StereoFoldDown = true });
        var fold = File.ReadAllBytes(folded);
        Near(Sample24(fold, 10, 0, 2), 0); Near(Sample24(fold, 10, 1, 2), .25 * Math.Sqrt(.5));
        var bed = new AudioBuffer(48000, 10, Enumerable.Range(0, 48 * 10).Select(i => (float)((i % 10 + 1) / 40d)).ToArray(), 0x2d60f);
        var bedTrack = Track("bed514.wav");
        double[] Out(SpeakerLayout layout)
        {
            var file = Path.Combine(root, $"bed514-{layout}.wav");
            var r = WaveRenderer.Render(file, Session(layout, bedTrack), [new(bedTrack, bed)]);
            var b = File.ReadAllBytes(file);
            return Enumerable.Range(0, r.Channels).Select(c => Sample24(b, 10, c, r.Channels)).ToArray();
        }
        double h = Math.Sqrt(.5);
        // Bed channels: L .025 R .05 C .075 LFE .1 Ls .125 Rs .15 Tfl .175 Tfr .2 Tbl .225 Tbr .25
        foreach (var (layout, expected) in new (SpeakerLayout, double[])[]
        {
            (SpeakerLayout.Surround514, Enumerable.Range(1, 10).Select(i => i / 40d).ToArray()),
            (SpeakerLayout.Surround714, [.025, .05, .075, .1, 0, 0, .125, .15, .175, .2, .225, .25]),
            (SpeakerLayout.Surround51, [.025 + .175 * h, .05 + .2 * h, .075, .1, .125 + .225 * h, .15 + .25 * h]),
        })
        {
            var actual = Out(layout);
            Equal(actual.Length, expected.Length);
            for (int c = 0; c < expected.Length; c++) Near(actual[c], expected[c]);
        }
    });
    Test("EQ bands match the RBJ responses, stay exact when flat and ramp live changes", () =>
    {
        const int rate = 48000;
        var peak = Biquad.Design(EqBandKind.Peak, new(true, 1000, 6, 1), rate);
        Near(peak.MagnitudeDb(1000, rate), 6, 1e-9); Near(peak.MagnitudeDb(20, rate), 0, .02);
        var hpf = Biquad.Design(EqBandKind.HighPass, new(true, 100), rate);
        Near(hpf.MagnitudeDb(100, rate), -3.01, .01); Check(hpf.MagnitudeDb(25, rate) < -23, "12 dB/oct below the corner");
        Near(Biquad.Design(EqBandKind.LowPass, new(true, 5000), rate).MagnitudeDb(5000, rate), -3.01, .01);
        var low = Biquad.Design(EqBandKind.LowShelf, new(true, 200, 4), rate);
        Near(low.MagnitudeDb(20, rate), 4, .1); Near(low.MagnitudeDb(200, rate), 2, .01); Near(low.MagnitudeDb(10000, rate), 0, .05);
        var high = Biquad.Design(EqBandKind.HighShelf, new(true, 4000, -6), rate);
        Near(high.MagnitudeDb(20000, rate), -6, .3); Near(high.MagnitudeDb(4000, rate), -3, .01); Near(high.MagnitudeDb(100, rate), 0, .05);
        Check(Biquad.Design(EqBandKind.Peak, new(true, 1000, 0, 1), rate).IsIdentity && Biquad.Design(EqBandKind.HighPass, new(false, 100), rate).IsIdentity,
            "Flat and bypassed bands are exact identities");
        var top = Biquad.Design(EqBandKind.LowPass, new(true, 20000, 0, 10), 44100);
        Check(Math.Abs(top.A2) < 1 && Math.Abs(top.A1) < 1 + top.A2, "A corner above 0.45 × rate is clamped and stays stable");
        Near(TrackEq.Flat.ResponseDb(1234, rate), 0, 0);

        var tone = new AudioBuffer(rate, 1, Enumerable.Range(0, 9600).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * 1000 * i / rate))).ToArray());
        var plain = Track("eq.wav");
        var boosted = plain with { Eq = TrackEq.Flat with { Peak1 = new(true, 1000, 6, 1) } };
        var neutral = plain with { Eq = TrackEq.Flat with { Peak1 = new(true, 1000, 0, 1), LowShelf = new(true, 100, 0) } };
        double[] Mix(TrackSettings t)
        {
            var m = new StreamingMixer(Session(SpeakerLayout.Stereo, t), [new(t, tone)]);
            var o = new double[m.TotalFrames * 2]; m.Read(o); return o;
        }
        var dry = Mix(plain);
        Check(dry.SequenceEqual(Mix(neutral)), "0 dB bands must not touch samples");
        var wet = Mix(boosted);
        double Rms(double[] s) => Math.Sqrt(s.Skip(4800 * 2).Where((_, i) => i % 2 == 0).Average(v => v * v));
        Near(20 * Math.Log10(Rms(wet) / Rms(dry)), 6, .05);

        var live = new StreamingMixer(Session(SpeakerLayout.Stereo, plain), [new(plain, tone)]);
        var before = new double[4800 * 2]; live.Read(before);
        live.Update(Session(SpeakerLayout.Stereo, boosted));
        var after = new double[live.RampFrames * 4 * 2]; live.Read(after);
        for (int f = 0; f < 10; f++) Near(after[f * 2], dry[(4800 + f) * 2], .002);
        for (int f = live.RampFrames * 2; f < live.RampFrames * 4; f++) Near(after[f * 2], wet[(4800 + f) * 2], .001);
    });
    Test("RF64 is written on request (and beyond 4 GiB) and reads back", () =>
    {
        var media = new AudioBuffer(48000, 1, Enumerable.Repeat(.25f, 480).ToArray());
        var track = Track("rf64.wav");
        var path = Path.Combine(root, "forced.rf64.wav");
        WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, track), [new(track, media)], new() { Rf64 = true, Dither = false });
        var bytes = File.ReadAllBytes(path);
        Equal(Encoding.ASCII.GetString(bytes, 0, 4), "RF64"); Equal(BitConverter.ToUInt32(bytes, 4), uint.MaxValue);
        Equal(Encoding.ASCII.GetString(bytes, 12, 4), "ds64"); Equal(BitConverter.ToUInt32(bytes, 16), 28u);
        Equal(BitConverter.ToInt64(bytes, 20), (long)bytes.Length - 8); Equal(BitConverter.ToInt64(bytes, 28), 480L * 6 * 3); Equal(BitConverter.ToInt64(bytes, 36), 480L);
        var back = WaveReader.Read(path);
        Equal(back.Frames, 480L); Equal(back.Channels, 6); Near(back[100, 2], .25, 1e-6);
        Equal(WaveReader.Read(path, stream: true)[479, 2], back[479, 2]);
    });
    Test("Delivery renders mix, multi-mono and stems in one pass and writes a QC report", () =>
    {
        var folder = Path.Combine(root, "delivery"); Directory.CreateDirectory(folder);
        var tone = new AudioBuffer(48000, 1, Enumerable.Range(0, 4800).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray());
        var noise = new AudioBuffer(48000, 1, Enumerable.Range(0, 2400).Select(i => (float)(.1 * Math.Sin(i * 1.7))).ToArray());
        var dx = Track("dx.wav") with { Group = "DX" };
        var mx = Track("mx.wav") with { Group = "MX", Azimuth = 110, StartSeconds = .02 };
        var fx = Track("fx.wav") with { Azimuth = -60, LfeDb = -6 };
        var session = Session(SpeakerLayout.Surround51, dx, mx, fx) with { Name = "Delivery test" };
        LoadedTrack[] loaded = [new(dx, tone), new(mx, noise), new(fx, noise)];
        var options = new DeliveryOptions
        {
            MultiMono = true, Stems = true, Target = LoudnessTarget.EbuR128, Export = new() { Dither = false, Created = new DateTime(2026, 11, 9, 12, 0, 0) }
        };
        var mixPath = Path.Combine(folder, "Film.wav");
        var planned = Delivery.PlannedFiles(mixPath, session, options);
        Equal(planned.Count, 1 + 6 + 3 + 2);
        var report = Delivery.Deliver(mixPath, session, loaded, options);
        foreach (var file in planned) Check(File.Exists(file), $"{Path.GetFileName(file)} is delivered");
        Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "No temporary files remain");
        var mixAudio = WaveReader.Read(mixPath);
        foreach (var (speaker, c) in SpatialMixer.Speakers(SpeakerLayout.Surround51).Select((s, c) => (s, c)))
        {
            var monoAudio = WaveReader.Read(Path.Combine(folder, $"Film.{speaker.Name}.wav"));
            Equal(monoAudio.Channels, 1); Equal(monoAudio.Frames, mixAudio.Frames);
            for (long f = 0; f < mixAudio.Frames; f += 97) Equal(monoAudio[f, 0], mixAudio[f, c]);
        }
        foreach (var stem in new[] { "DX", "MX", "Other" }) Equal(WaveReader.Read(Path.Combine(folder, $"Film_{stem}.wav")).Frames, mixAudio.Frames);
        Check(report.Checks.Single(c => c.Name == "Stems sum to the mix").Passed, "Stems must sum to the mix");
        Check(report.Checks.Single(c => c.Name == "No track is soloed").Passed, "Nothing is soloed");
        Check(!report.Checks.Single(c => c.Name.StartsWith("Integrated")).Passed && !report.Passed, "A quiet test mix misses the R128 target");
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(planned[^2])))
        {
            var first = doc.RootElement.GetProperty("Files")[0];
            Equal(first.GetProperty("Sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(mixPath))));
            Equal(doc.RootElement.GetProperty("Passed").GetBoolean(), false);
        }
        Check(File.ReadAllText(planned[^1]).Contains("FAIL  Integrated loudness -23 ±1 LUFS"), "The text report lists the failed check");
        Check(File.ReadAllBytes(planned[^1]).AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "The text report starts with a UTF-8 BOM");
        Check(File.ReadAllBytes(planned[^2])[0] == (byte)'{', "The JSON report has no BOM");
        Equal(report.Files.Single(f => f.Name == "Film.LFE.wav").SilentChannels.Length, 0);
        Throws<IOException>(() => Delivery.Deliver(mixPath, session, loaded, options));
        var soloDx = dx with { Solo = true };
        var again = Delivery.Deliver(mixPath, session with { Tracks = [soloDx, mx, fx] }, [new(soloDx, tone), new(mx, noise), new(fx, noise)], options with { Overwrite = true });
        Check(!again.Checks.Single(c => c.Name == "No track is soloed").Passed && !again.Checks.Single(c => c.Name == "Stems sum to the mix").Passed,
            "Solo is flagged and breaks the stem sum");
        Throws<InvalidDataException>(() => SessionStore.Validate(session with { Tracks = [dx with { Group = "D/X" }] }));
        Throws<InvalidDataException>(() => SessionStore.Validate(session with { Tracks = [dx with { Group = "a.b" }] }));
    });
    Test("Session v2 stores elevation, spread and height layouts; v1 files still load", () =>
    {
        var v1 = Path.Combine(root, "v1.xsurlab.json");
        File.WriteAllText(v1, """
            { "Version": 1, "Name": "Old", "SampleRate": 48000, "Layout": "Surround51", "MasterGainDb": -6,
              "Tracks": [ { "Id": "4f9c2f8e-1d1b-4a39-9a51-0c3c9f6b2a11", "Name": "Old track", "MediaPath": "old.wav",
                "GainDb": 0, "Azimuth": 30, "LfeDb": -60, "StartSeconds": 0, "Mute": false, "Solo": false } ] }
            """);
        var old = SessionStore.Load(v1);
        Equal(old.Version, SessionStore.CurrentVersion); Equal(old.Tracks[0].Elevation, 0d); Equal(old.Tracks[0].Spread, 0d);
        Equal(old.Tracks[0].MediaPath, Path.Combine(root, "old.wav"));
        var raised = Session(SpeakerLayout.Surround714, Track("lifted.wav") with
        {
            Elevation = 30, Spread = 90, Eq = TrackEq.Flat with { HighPass = new(true, 90, 0, .9), Peak2 = new(true, 2500, -3.5, 2.2) }
        });
        var saved = Path.Combine(root, "v2.xsurlab.json"); SessionStore.Save(saved, raised);
        var json = File.ReadAllText(saved);
        Check(json.Contains("\"Version\": 2") && json.Contains("\"Surround714\"") && json.Contains("\"Spread\": 90") && json.Contains("\"Peak2\""), "v2 is written with the new fields");
        Equal(SessionStore.Load(saved).Tracks.Single(), raised.Tracks.Single());
        Throws<InvalidDataException>(() => SessionStore.Validate(raised with { Tracks = [raised.Tracks[0] with { Elevation = 91 }] }));
        Throws<InvalidDataException>(() => SessionStore.Validate(raised with { Tracks = [raised.Tracks[0] with { Spread = -1 }] }));
        Throws<InvalidDataException>(() => SessionStore.Validate(raised with { Version = 3 }));
        var eq = raised.Tracks[0].Eq;
        foreach (var bad in new[] { eq with { Peak1 = new(true, 10) }, eq with { Peak1 = new(true, 1000, 30) }, eq with { LowPass = new(true, 9000, 0, 0) }, null! })
            Throws<InvalidDataException>(() => SessionStore.Validate(raised with { Tracks = [raised.Tracks[0] with { Eq = bad }] }));
    });
    Test("5.1 / 7.1 exports carry the mask, byte count and center channel in order", () =>
    {
        var media = WaveReader.Read(Fixture("center.wav", 1, 16, 1, 48, (_, _) => .25));
        foreach (var (layout, channels, mask) in new[] { (SpeakerLayout.Surround51, 6, 0x60fu), (SpeakerLayout.Surround51Back, 6, 0x3fu), (SpeakerLayout.Surround71, 8, 0x63fu), (SpeakerLayout.Surround514, 10, 0x2d60fu), (SpeakerLayout.Surround714, 12, 0x2d63fu) })
        {
            var track = Track("center.wav");
            var path = Path.Combine(root, $"{channels}ch.wav");
            var result = WaveRenderer.Render(path, Session(layout, track), [new(track, media)]);
            var bytes = File.ReadAllBytes(path); var chunks = Chunks(bytes); int fmt = chunks["fmt "].Offset;
            Equal(BitConverter.ToUInt16(bytes, fmt), (ushort)0xfffe);
            Equal(BitConverter.ToUInt16(bytes, fmt + 2), (ushort)channels);
            Equal(BitConverter.ToUInt16(bytes, fmt + 14), (ushort)24);
            Equal(BitConverter.ToUInt32(bytes, fmt + 20), mask);
            Equal(chunks["data"].Size, 48 * channels * 3);
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
        var result = WaveRenderer.Render(path, Session(SpeakerLayout.Surround51, track), [new(track, media)], new() { StereoFoldDown = true, Format = SampleFormat.Pcm16, Dither = false });
        Equal(result.Frames, 480L);
        var audio = WaveReader.Read(path);
        Equal(audio.SampleRate, 48000); Equal(audio.Channels, 2); Near(audio.Duration, .01);
        Near(audio[240, 0], .25 * Math.Sqrt(.5), .00004);
    });
    Test("Windowed-sinc rate conversion tracks a 1 kHz tone within -80 dB", () =>
    {
        var tone = new AudioBuffer(44100, 1, Enumerable.Range(0, 44100).Select(i => (float)(.5 * Math.Sin(2 * Math.PI * 1000 * i / 44100))).ToArray());
        var resampler = new Resampler(44100, 48000); var value = new double[1]; double worst = 0;
        for (int n = 200; n < 47800; n++)
        {
            resampler.Read(tone.OpenReader(), n * resampler.Ratio, value);
            worst = Math.Max(worst, Math.Abs(value[0] - .5 * Math.Sin(2 * Math.PI * 1000 * n / 48000d)));
        }
        Console.WriteLine($"  1 kHz 44.1→48 kHz worst error {20 * Math.Log10(worst / .5):F1} dB");
        Check(worst < .5e-4, "Tone error must stay below -80 dB");
    });
    Test("Downsampling removes content above the new Nyquist limit and keeps DC exact", () =>
    {
        var high = new AudioBuffer(96000, 1, Enumerable.Range(0, 96000).Select(i => (float)(.5 * Math.Sin(2 * Math.PI * 30000 * i / 96000))).ToArray());
        var resampler = new Resampler(96000, 48000); var value = new double[1]; double energy = 0;
        for (int n = 200; n < 47800; n++) { resampler.Read(high.OpenReader(), n * resampler.Ratio, value); energy += value[0] * value[0]; }
        double rms = Math.Sqrt(energy / 47600), reference = .5 / Math.Sqrt(2);
        Console.WriteLine($"  30 kHz 96→48 kHz residue {20 * Math.Log10(rms / reference):F1} dB");
        Check(rms < reference * 1e-4, "Alias residue must stay below -80 dB");
        var dc = new AudioBuffer(44100, 2, Enumerable.Repeat(.25f, 8820).ToArray());
        var pair = new double[2]; new Resampler(44100, 48000).Read(dc.OpenReader(), 2000.37, pair);
        Near(pair[0], .25, 1e-9); Near(pair[1], .25, 1e-9);
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
    Test("Streaming is chunk-invariant with mixed rates, offsets and LFE filter history", () =>
    {
        var audio = new AudioBuffer(44100, 2, Enumerable.Range(0, 1800).Select(i => (float)(.1 * Math.Sin(i * .071))).ToArray());
        var track = Track("stream.wav") with
        {
            LfeDb = -3, StartSeconds = .001, Azimuth = 93, Elevation = 20, Spread = 40,
            Eq = TrackEq.Flat with { HighPass = new(true, 60), Peak1 = new(true, 700, 6, 2), HighShelf = new(true, 6000, -4) }
        };
        foreach (var layout in Enum.GetValues<SpeakerLayout>())
        foreach (bool stereo in new[] { false, true })
        {
            var session = Session(layout, track);
            var a = new StreamingMixer(session, [new(track, audio)], stereo);
            var whole = new double[(int)a.TotalFrames * a.Channels];
            Equal(a.Read(whole), (int)a.TotalFrames);
            var b = new StreamingMixer(session, [new(track, audio)], stereo);
            var chunks = new List<double>();
            var block = new double[17 * b.Channels];
            while (b.Position < b.TotalFrames)
            {
                int count = b.Read(block) * b.Channels;
                chunks.AddRange(block.Take(count));
            }
            Check(whole.SequenceEqual(chunks), "Chunk boundaries must preserve all samples exactly");
            Near(a.Peak, b.Peak);
            var path = Path.Combine(root, $"stream-{layout}-{stereo}.wav");
            WaveRenderer.Render(path, session, [new(track, audio)], new() { StereoFoldDown = stereo, Format = stereo ? SampleFormat.Pcm16 : SampleFormat.Pcm24, Dither = false });
            var bytes = File.ReadAllBytes(path);
            int header = DataOffset(bytes);
            for (int i = 0; i < whole.Length; i++)
            {
                double pcm = stereo ? BitConverter.ToInt16(bytes, header + i * 2) / 32768d
                    : ((bytes[header + i * 3] | bytes[header + i * 3 + 1] << 8 | bytes[header + i * 3 + 2] << 16) << 8 >> 8) / 8388608d;
                Near(whole[i], pcm, stereo ? 1d / 32768 : 1d / 8388608);
            }
        }
    });
    Test("Live gain, master and pan ramp in on the next block without rewinding", () =>
    {
        var audio = new AudioBuffer(48000, 1, Enumerable.Repeat(.25f, 4000).ToArray());
        var track = Track("live.wav"); var session = Session(SpeakerLayout.Surround51, track);
        var mixer = new StreamingMixer(session, [new(track, audio)]);
        var block = new double[6]; mixer.Read(block); Near(block[2], .25);
        var changed = track with { Azimuth = 110, GainDb = -6 };
        mixer.Update(session with { MasterGainDb = -6, Tracks = [changed] });
        mixer.Read(block); Equal(mixer.Position, 2L);
        Check(block[2] > .249 && block[5] < .001, "The first frame after a change must stay next to the old value");
        var ramp = new double[mixer.RampFrames * 6]; mixer.Read(ramp);
        for (int f = 1; f < mixer.RampFrames; f++)
            Check(ramp[f * 6 + 2] <= ramp[(f - 1) * 6 + 2] && ramp[f * 6 + 5] >= ramp[(f - 1) * 6 + 5], "Ramp must be monotonic");
        mixer.Read(block);
        Near(block[5], .25 * Math.Pow(10, -.6)); Near(block[2], 0);
    });
    Test("Mute fades over 20 ms instead of stepping", () =>
    {
        var audio = new AudioBuffer(48000, 1, Enumerable.Repeat(.5f, 6000).ToArray());
        var track = Track("fade.wav"); var session = Session(SpeakerLayout.Surround51, track);
        var mixer = new StreamingMixer(session, [new(track, audio)]);
        Equal(mixer.RampFrames, 960);
        var output = new double[3000 * 6];
        mixer.Read(output.AsSpan(0, 1000 * 6));
        mixer.Update(session with { Tracks = [track with { Mute = true }] });
        mixer.Read(output.AsSpan(1000 * 6));
        double largest = 0;
        for (int f = 1; f < 3000; f++) largest = Math.Max(largest, Math.Abs(output[f * 6 + 2] - output[(f - 1) * 6 + 2]));
        Check(largest <= .5 / 960 * 1.0001, $"Largest step {largest} must be one ramp increment");
        Near(output[1000 * 6 + 2], .5 * 959 / 960); Equal(output[2999 * 6 + 2], 0d);
    });
    Test("Live mute and solo can silence and restore all tracks without stopping", () =>
    {
        var audio = new AudioBuffer(48000, 1, Enumerable.Repeat(.1f, 8000).ToArray());
        var a = Track("a.wav") with { Azimuth = -30 }; var b = Track("b.wav") with { Azimuth = 30 };
        var session = Session(SpeakerLayout.Surround51, a, b);
        var mixer = new StreamingMixer(session, [new(a, audio), new(b, audio)]);
        var block = new double[6]; var ramp = new double[mixer.RampFrames * 6];
        mixer.Read(block); Near(block[0], .1); Near(block[1], .1);
        mixer.Update(session with { Tracks = [a with { Mute = true, Solo = true }, b] });
        mixer.Read(ramp); mixer.Read(block); Check(block.All(x => x == 0), "A muted solo track must suppress the other tracks");
        mixer.Update(session with { Tracks = [a, b with { Solo = true }] });
        mixer.Read(ramp); mixer.Read(block); Near(block[0], 0); Near(block[1], .1);
        mixer.Update(session); mixer.Read(ramp); mixer.Read(block); Near(block[0], .1); Near(block[1], .1);
    });
    Test("Enabling LFE live uses continuous low-pass history", () =>
    {
        var audio = new AudioBuffer(48000, 1, Enumerable.Repeat(.1f, 4000).ToArray());
        var off = Track("bass.wav"); var on = off with { LfeDb = 0 };
        var a = new StreamingMixer(Session(SpeakerLayout.Surround51, off), [new(off, audio)]);
        var b = new StreamingMixer(Session(SpeakerLayout.Surround51, on), [new(on, audio)]);
        a.Read(new double[100 * 6]); b.Read(new double[100 * 6]);
        a.Update(Session(SpeakerLayout.Surround51, on));
        a.Read(new double[a.RampFrames * 6]); b.Read(new double[b.RampFrames * 6]);
        var x = new double[6]; var y = new double[6]; a.Read(x); b.Read(y); Equal(x[3], y[3]);
    });
    Test("Streaming rejects structural changes atomically and handles final partial blocks", () =>
    {
        var audio = new AudioBuffer(48000, 1, Enumerable.Repeat(.25f, 3).ToArray());
        var track = Track("short.wav"); var session = Session(SpeakerLayout.Surround51, track);
        var mixer = new StreamingMixer(session, [new(track, audio)]);
        Throws<InvalidOperationException>(() => mixer.Update(session with { Layout = SpeakerLayout.Stereo }));
        Throws<InvalidOperationException>(() => mixer.Update(session with { SampleRate = 96000 }));
        Throws<InvalidOperationException>(() => mixer.Update(session with { Tracks = [track with { StartSeconds = 1 }] }));
        Throws<InvalidOperationException>(() => mixer.Update(session with { Tracks = [track with { Id = Guid.NewGuid() }] }));
        Throws<InvalidOperationException>(() => mixer.Update(session with { Tracks = [track with { MediaPath = "elsewhere.wav" }] }));
        var block = Enumerable.Repeat(99d, 30).ToArray();
        Equal(mixer.Read(block), 3); Near(block[2], .25); Check(block.Skip(18).All(x => x == 0), "Tail must be cleared");
        Equal(mixer.Read(block), 0); Check(block.All(x => x == 0), "EOF must clear stale audio");
        Throws<ArgumentException>(() => mixer.Read(new double[7]));
    });
    Test("Streaming cancellation and invalid samples never submit a broken block", () =>
    {
        var track = Track("cancel-live.wav");
        var session = Session(SpeakerLayout.Surround51, track);
        var mixer = new StreamingMixer(session, [new(track, new AudioBuffer(48000, 1, new float[10]))]);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => mixer.Read(new double[6], cancel.Token)); Equal(mixer.Position, 0L);
        var invalid = new StreamingMixer(session, [new(track, new AudioBuffer(48000, 1, [float.NaN]))]);
        Throws<InvalidDataException>(() => invalid.Read(new double[6]));
    });
    Test("Seek jumps on the next read, fades in over 20 ms and reopens a finished stream", () =>
    {
        var audio = new AudioBuffer(48000, 1, Enumerable.Range(0, 20000).Select(i => (float)(.1 + i * 1e-5)).ToArray());
        var track = Track("seek.wav"); var session = Session(SpeakerLayout.Surround51, track);
        var reference = new StreamingMixer(session, [new(track, audio)]);
        var all = new double[20000 * 6]; Equal(reference.Read(all), 20000);
        var mixer = new StreamingMixer(session, [new(track, audio)]);
        mixer.Read(new double[100 * 6]);
        var thread = new Thread(() => mixer.Seek(12000)); thread.Start(); thread.Join();
        Check(mixer.SeekPending, "A seek waits for the next read"); Equal(mixer.Position, 100L);
        var after = new double[3000 * 6];
        Equal(mixer.Read(after), 3000); Equal(mixer.Position, 15000L); Check(!mixer.SeekPending, "The read consumes the seek");
        Check(after[2] > 0 && after[2] < all[12000 * 6 + 2] * .01, "The first frame after a seek starts near silence");
        for (int f = 1; f < mixer.RampFrames; f++) Check(after[f * 6 + 2] >= after[(f - 1) * 6 + 2], "The fade-in must be monotonic");
        for (int f = 2 * mixer.RampFrames; f < 3000; f++) Equal(after[f * 6 + 2], all[(12000 + f) * 6 + 2]);
        mixer.Read(new double[20000 * 6]); Check(mixer.Finished, "Reading past the end finishes the stream");
        mixer.Seek(0); Check(!mixer.Finished, "A pending seek reopens a finished stream");
        Equal(mixer.Read(new double[6]), 1); Equal(mixer.Position, 1L);
        Throws<ArgumentOutOfRangeException>(() => mixer.Seek(-1));
        Throws<ArgumentOutOfRangeException>(() => mixer.Seek(mixer.TotalFrames + 1));
        mixer.Seek(mixer.TotalFrames); Equal(mixer.Read(new double[6]), 0); Check(mixer.Finished, "A seek to the end reads nothing");
    });
    Test("Edit history undoes, redoes, drops the redo branch and keeps a bounded number of steps", () =>
    {
        var history = new EditHistory<string>("a", limit: 3);
        Check(!history.CanUndo && !history.CanRedo, "A new history has nothing to undo or redo");
        Check(!history.Record("a", "same"), "An unchanged state is not a step");
        history.Record("b", "to b"); history.Record("c", "to c");
        Equal(history.UndoLabel, "to c"); Equal(history.Undo(), "b");
        Equal(history.RedoLabel, "to c"); Equal(history.Redo(), "c");
        history.Undo(); history.Record("d", "to d");
        Check(!history.CanRedo, "A new edit drops the redo branch"); Equal(history.Current, "d");
        history.Record("e", "to e"); Equal(history.Count, 3);
        history.Undo(); Equal(history.Undo(), "b"); Check(!history.CanUndo, "The oldest step falls off at the limit");
        Throws<InvalidOperationException>(() => history.Undo());
        history.Reset("z"); Equal(history.Current, "z"); Check(!history.CanUndo && !history.CanRedo, "Reset clears both directions");
    });
    Test("K-weighting reproduces the BS.1770 48 kHz coefficients", () =>
    {
        var (b1, a1, b2, a2) = LoudnessMeter.KWeighting(48000);
        double[] published = [1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, .73248077421585, -1.99004745483398, .99007225036621];
        double[] actual = [b1[0], b1[1], b1[2], a1[1], a1[2], a2[1], a2[2]];
        for (int i = 0; i < published.Length; i++) Near(actual[i], published[i], 1e-8);
        Check(b2.SequenceEqual(new double[] { 1, -2, 1 }), "RLB numerator must be 1, -2, 1");
    });
    Test("Integrated loudness matches EBU Tech 3341 cases within 0.1 LU", () =>
    {
        double[] Pair(double db) => [db, db];
        var results = new (string Name, double Lufs, double Expected)[]
        {
            ("1 kHz -23 dBFS 48 kHz", Measure(48000, SpeakerLayout.Stereo, (20, Pair(-23))).Integrated, -23),
            ("1 kHz -23 dBFS 44.1 kHz", Measure(44100, SpeakerLayout.Stereo, (20, Pair(-23))).Integrated, -23),
            ("1 kHz -23 dBFS 96 kHz", Measure(96000, SpeakerLayout.Stereo, (20, Pair(-23))).Integrated, -23),
            ("1 kHz -33 dBFS", Measure(48000, SpeakerLayout.Stereo, (20, Pair(-33))).Integrated, -33),
            ("-36/-23/-36 relative gate", Measure(48000, SpeakerLayout.Stereo, (10, Pair(-36)), (60, Pair(-23)), (10, Pair(-36))).Integrated, -23),
            ("-72/-36/-23/-36/-72 both gates", Measure(48000, SpeakerLayout.Stereo, (10, Pair(-72)), (10, Pair(-36)), (60, Pair(-23)), (10, Pair(-36)), (10, Pair(-72))).Integrated, -23),
            ("5.0 L/R -28, C -24, Ls/Rs -30", Measure(48000, SpeakerLayout.Surround51, (20, [-28, -28, -24, double.NegativeInfinity, -30, -30])).Integrated, -23),
        };
        foreach (var (name, lufs, expected) in results)
        {
            Console.WriteLine($"  {name}: {lufs:F2} LUFS");
            Near(lufs, expected, .1);
        }
    });
    Test("Loudness range matches EBU Tech 3342 cases within 1 LU", () =>
    {
        double[] Pair(double db) => [db, db];
        foreach (var (low, high, expected) in new[] { (-30d, -20d, 10d), (-20d, -15d, 5d), (-40d, -20d, 20d) })
        {
            double range = Measure(48000, SpeakerLayout.Stereo, (20, Pair(high)), (20, Pair(low))).Range;
            Console.WriteLine($"  {high}/{low} dBFS: LRA {range:F2} LU");
            Near(range, expected, 1);
        }
        double steps = Measure(48000, SpeakerLayout.Stereo, (20, Pair(-50)), (20, Pair(-35)), (20, Pair(-20)), (20, Pair(-35)), (20, Pair(-50))).Range;
        Console.WriteLine($"  -50/-35/-20/-35/-50 dBFS: LRA {steps:F2} LU");
        Near(steps, 15, 1);
    });
    Test("True peak catches inter-sample peaks within +0.2/-0.4 dB", () =>
    {
        double worst = 0, largestGap = 0;
        foreach (int divisor in new[] { 4, 6, 8 })
        for (double phase = 0; phase < 90; phase += 7.5)
        {
            var meter = new LoudnessMeter(48000, SpatialMixer.Speakers(SpeakerLayout.Stereo));
            // A 10 ms fade-in keeps the reconstruction overshoot of an abrupt onset out of the steady-state check.
            var block = Enumerable.Range(0, 4800).SelectMany(n =>
            {
                double fade = n < 480 ? .5 - .5 * Math.Cos(Math.PI * n / 480) : 1;
                double v = .5 * fade * Math.Sin(2 * Math.PI * n / divisor + phase * Math.PI / 180);
                return new[] { v, v };
            }).ToArray();
            meter.Process(block);
            var report = meter.Report();
            double error = report.TruePeakDb - 20 * Math.Log10(.5);
            Check(error is <= .2 and >= -.4, $"fs/{divisor} at {phase}° reads {error:F2} dB from the true peak");
            worst = Math.Abs(error) > Math.Abs(worst) ? error : worst;
            largestGap = Math.Max(largestGap, report.TruePeakDb - report.SamplePeakDb);
        }
        Console.WriteLine($"  worst true-peak error {worst:F2} dB; largest gap above sample peak {largestGap:F2} dB");
        // fs/4 at 45° puts the crest half-way between samples (3.01 dB above them); 4× phases land within 11.25° (−0.17 dB).
        Check(largestGap > 2.8, "fs/4 at 45° must read close to 3 dB above its sample peak");
    });
    Test("LFE is excluded from loudness and silence reads minus infinity", () =>
    {
        var lfeOnly = Measure(48000, SpeakerLayout.Surround51, (5, [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, -10, double.NegativeInfinity, double.NegativeInfinity]));
        Check(double.IsNegativeInfinity(lfeOnly.Integrated), "LFE alone must not register loudness");
        Near(lfeOnly.ChannelPeaks[3], Math.Pow(10, -.5), 1e-6);
        var silent = new LoudnessMeter(48000, SpatialMixer.Speakers(SpeakerLayout.Stereo));
        silent.Process(new double[96000]);
        Check(double.IsNegativeInfinity(silent.Report().Integrated) && silent.Report().Range == 0, "Silence must not register loudness");
    });
    Test("Meter results and mixer metering do not depend on block size", () =>
    {
        var signal = Enumerable.Range(0, 48000 * 6 * 2).Select(i => .3 * Math.Sin(i * .013) * Math.Sin(i * .00007)).ToArray();
        var speakers = SpatialMixer.Speakers(SpeakerLayout.Surround51);
        var a = new LoudnessMeter(48000, speakers); a.Process(signal);
        var b = new LoudnessMeter(48000, speakers);
        for (int i = 0; i < signal.Length; i += 6 * 997) b.Process(signal.AsSpan(i, Math.Min(6 * 997, signal.Length - i)));
        var x = a.Report(); var y = b.Report();
        Equal(x.Integrated, y.Integrated); Equal(x.Range, y.Range); Equal(x.TruePeak, y.TruePeak); Equal(a.Frames, 96000L);
        var media = new AudioBuffer(48000, 1, Enumerable.Range(0, 48000).Select(i => (float)(.2 * Math.Sin(i * .1))).ToArray());
        var track = Track("metered.wav"); var session = Session(SpeakerLayout.Surround51, track);
        var mixer = new StreamingMixer(session, [new(track, media)]);
        mixer.Read(new double[mixer.TotalFrames * 6]);
        var path = Path.Combine(root, "metered-out.wav");
        var result = WaveRenderer.Render(path, session, [new(track, media)]);
        Equal(result.Loudness.Integrated, mixer.Meter.Report().Integrated);
        Check(double.IsFinite(result.Loudness.Integrated) && mixer.Meter.Latest is { Frames: 48000 }, "Render and mixer must report the same measurement");
    });
    Test("PCM16, PCM24 and float32 exports have valid RIFF structure and read back", () =>
    {
        var media = new AudioBuffer(48000, 1, Enumerable.Range(0, 4800).Select(i => (float)(.4 * Math.Sin(i * .05))).ToArray());
        var track = Track("formats.wav");
        foreach (var layout in new[] { SpeakerLayout.Stereo, SpeakerLayout.Surround51 })
        foreach (var format in Enum.GetValues<SampleFormat>())
        {
            var path = Path.Combine(root, $"format-{layout}-{format}.wav");
            WaveRenderer.Render(path, Session(layout, track), [new(track, media)], new() { Format = format });
            var bytes = File.ReadAllBytes(path); var chunks = Chunks(bytes);
            int fmt = chunks["fmt "].Offset, channels = layout == SpeakerLayout.Stereo ? 2 : 6;
            int bits = format switch { SampleFormat.Pcm16 => 16, SampleFormat.Pcm24 => 24, _ => 32 };
            Equal((int)BitConverter.ToUInt16(bytes, fmt + 14), bits);
            Equal((int)BitConverter.ToUInt16(bytes, fmt + 12), channels * bits / 8);
            Equal(chunks["data"].Size, 4800 * channels * bits / 8);
            Check(chunks.ContainsKey("fact") == (format == SampleFormat.Float32), "Only float output carries a fact chunk");
            if (format == SampleFormat.Float32) Equal(BitConverter.ToUInt32(bytes, chunks["fact"].Offset), 4800u);
            Check(chunks.ContainsKey("bext"), "Broadcast WAV is on by default");
            if (channels != 2) continue;
            var back = WaveReader.Read(path);
            double tolerance = format switch { SampleFormat.Pcm16 => 2d / 32768, SampleFormat.Pcm24 => 2d / 8388608, _ => 1e-7 };
            for (int f = 0; f < 4800; f += 37) Near(back[f, 0], media[f, 0] * Math.Sqrt(.5), tolerance);
        }
    });
    Test("TPDF dither is seeded, bounded to ±1 LSB and absent without the option", () =>
    {
        var media = new AudioBuffer(48000, 1, Enumerable.Repeat(.25f, 2000).ToArray());
        var track = Track("dither.wav"); var session = Session(SpeakerLayout.Stereo, track);
        short[] Pcm16(string name, bool dither)
        {
            var path = Path.Combine(root, name);
            WaveRenderer.Render(path, session, [new(track, media)], new() { Format = SampleFormat.Pcm16, Dither = dither, BroadcastWave = false });
            var bytes = File.ReadAllBytes(path); int data = DataOffset(bytes);
            return Enumerable.Range(0, 4000).Select(i => BitConverter.ToInt16(bytes, data + i * 2)).ToArray();
        }
        var plain = Pcm16("plain16.wav", false); var a = Pcm16("dither-a.wav", true); var b = Pcm16("dither-b.wav", true);
        double exact = .25 * Math.Sqrt(.5) * 32768; int code = (int)Math.Round(exact);
        Check(plain.All(s => s == code), "Without dither every sample rounds to the same code");
        Check(a.All(s => Math.Abs(s - code) <= 1) && a.Distinct().Count() == 3, "TPDF dither spans ±1 LSB");
        Check(a.SequenceEqual(b), "The same seed must reproduce the same dither");
        Near(a.Average(s => (double)s), exact, .05);
    });
    Test("Broadcast WAV bext stores the description, version 2 and the measured loudness", () =>
    {
        var media = new AudioBuffer(48000, 1, Enumerable.Range(0, 48000 * 3).Select(i => (float)(.3 * Math.Sin(2 * Math.PI * 1000 * i / 48000))).ToArray());
        var track = Track("bwf.wav"); var session = Session(SpeakerLayout.Surround51, track) with { Name = "Reel 1 · A" };
        var path = Path.Combine(root, "bwf-out.wav");
        var result = WaveRenderer.Render(path, session, [new(track, media)], new() { Created = new DateTime(2026, 11, 9, 21, 5, 7) });
        var bytes = File.ReadAllBytes(path); var (offset, size) = Chunks(bytes)["bext"];
        string Text(int at, int length) => Encoding.ASCII.GetString(bytes, offset + at, length).TrimEnd('\0');
        short Loudness(int index) => BitConverter.ToInt16(bytes, offset + 412 + index * 2);
        Equal(Text(0, 256), "Reel 1 ? A"); Equal(Text(256, 32), "XSurLab 0.3");
        Equal(Text(320, 10), "2026-11-09"); Equal(Text(330, 8), "21:05:07");
        Equal(BitConverter.ToUInt16(bytes, offset + 346), (ushort)2);
        Console.WriteLine($"  center 1 kHz at -10.5 dBFS: {result.Loudness.Integrated:F2} LUFS, {result.Loudness.TruePeakDb:F2} dBTP");
        Near(result.Loudness.Integrated, 20 * Math.Log10(.3) - 3.01, .1);
        Equal(Loudness(0), (short)Math.Round(result.Loudness.Integrated * 100));
        Equal(Loudness(1), (short)Math.Round(result.Loudness.Range * 100));
        Equal(Loudness(2), (short)Math.Round(result.Loudness.TruePeakDb * 100));
        Equal(Loudness(3), (short)Math.Round(result.Loudness.MaxMomentary * 100));
        Equal(Loudness(4), (short)Math.Round(result.Loudness.MaxShortTerm * 100));
        Check(Text(602, size - 602).StartsWith("A=PCM,F=48000,W=24,M=multichannel,T=XSurLab 0.3"), "Coding history must describe the PCM format");
        var plain = Path.Combine(root, "plain-out.wav");
        WaveRenderer.Render(plain, session, [new(track, media)], new() { BroadcastWave = false });
        Check(!Chunks(File.ReadAllBytes(plain)).ContainsKey("bext"), "bext can be switched off");
    });
    Test("Multichannel WAV keeps its channel identity; ambiguous layouts are refused", () =>
    {
        var side = WaveReader.Read(Fixture("bed-side.wav", 6, 24, 1, 8, (_, c) => c / 10d, 0x60f));
        Equal(side.ChannelMask, 0x60fu); Near(side[0, 4], .4, 1e-6);
        Equal(WaveReader.Read(Fixture("bed-plain.wav", 6, 16, 1, 8, (_, _) => .1)).ChannelMask, 0x3fu);
        Equal(WaveReader.Read(Fixture("bed-714.wav", 12, 16, 1, 8, (_, _) => .1, 0x2d63f)).Channels, 12);
        Throws<InvalidDataException>(() => WaveReader.Read(Fixture("bed-badmask.wav", 6, 16, 1, 8, (_, _) => .1, 0x3)));
        Throws<InvalidDataException>(() => WaveReader.Read(Fixture("bed-unknown-bit.wav", 1, 16, 1, 8, (_, _) => .1, 0x40000)));
    });
    Test("Beds route channel for channel and fold between 5.1 Side, 5.1 Back, 7.1 and stereo", () =>
    {
        var bed = new AudioBuffer(48000, 6, Enumerable.Range(0, 48 * 6).Select(i => (float)((i % 6 + 1) / 10d)).ToArray(), 0x60f);
        var track = Track("bed51.wav") with { Azimuth = 90, LfeDb = 0 };
        double[] Render(SpeakerLayout layout, AudioBuffer media)
        {
            var path = Path.Combine(root, $"bed-{layout}-{media.Channels}.wav");
            var result = WaveRenderer.Render(path, Session(layout, track), [new(track, media)]);
            var bytes = File.ReadAllBytes(path);
            return Enumerable.Range(0, result.Channels).Select(c => Sample24(bytes, 10, c, result.Channels)).ToArray();
        }
        double h = Math.Sqrt(.5);
        foreach (var (layout, expected) in new (SpeakerLayout, double[])[]
        {
            (SpeakerLayout.Surround51, [.1, .2, .3, .4, .5, .6]),
            (SpeakerLayout.Surround51Back, [.1, .2, .3, .4, .5, .6]),
            (SpeakerLayout.Surround71, [.1, .2, .3, .4, 0, 0, .5, .6]),
            (SpeakerLayout.Stereo, [.1 + (.3 + .5) * h, .2 + (.3 + .6) * h]),
        })
        {
            var actual = Render(layout, bed);
            for (int c = 0; c < expected.Length; c++) Near(actual[c], expected[c]);
        }
        var bed71 = new AudioBuffer(48000, 8, Enumerable.Range(0, 48 * 8).Select(i => (float)((i % 8 + 1) / 20d)).ToArray(), 0x63f);
        var folded = Render(SpeakerLayout.Surround51, bed71);
        double[] fold = [.05, .10, .15, .20, .25 + .35, .30 + .40];
        for (int c = 0; c < 6; c++) Near(folded[c], fold[c]);
        foreach (var layout in Enum.GetValues<SpeakerLayout>())
        {
            var speakers = SpatialMixer.Speakers(layout);
            foreach (var bit in SpatialMixer.Bits(0x3ffff))
            {
                var route = SpatialMixer.Route(layout, bit);
                double power = route.Sum(g => g * g);
                int index = Array.FindIndex(speakers, s => s.Bit == bit);
                if (index >= 0) Check(route[index] == 1 && power == 1, $"{layout} must pass {SpatialMixer.ChannelName(bit)} straight through");
                else if (bit == SpatialMixer.LowFrequency) Equal(power, 0d);
                else Check(power is > .2 and <= 1 + 1e-9, $"{layout} folds {SpatialMixer.ChannelName(bit)} with power {power:F3}");
            }
        }
    });
    Test("Streamed media matches decoded media, validates floats, unlocks refused files and lifts the old 64 MiB limit", () =>
    {
        foreach (var (format, bits) in new[] { (1, 16), (1, 24), (1, 32), (3, 32) })
        {
            var path = Fixture($"mapped-{format}-{bits}.wav", 3, bits, format, 1000, (f, c) => Math.Sin(f * .01 + c) * .7, 0x7);
            var decoded = WaveReader.Read(path, stream: false); var mapped = WaveReader.Read(path, stream: true);
            Check(mapped.IsStreamed && !decoded.IsStreamed && mapped.MemoryBytes == 0, "stream selects the storage");
            Equal(mapped.ChannelMask, 0x7u); Equal(mapped.Frames, 1000L);
            for (long f = 0; f < 1000; f++) for (int c = 0; c < 3; c++) Equal(mapped[f, c], decoded[f, c]);
            Check(mapped.Overview.SequenceEqual(decoded.Overview), "Overviews must match");
        }
        var nan = Fixture("mapped-nan.wav", 1, 32, 3, 8, (_, _) => double.NaN);
        Throws<InvalidDataException>(() => WaveReader.Read(nan, stream: true));
        File.Delete(nan); // a refused import must release the file at once, not at the next garbage collection
        var large = Path.Combine(root, "large.wav");
        const int frames = 33 * 1024 * 1024;
        using (var writer = new BinaryWriter(File.Create(large)))
        {
            void Tag(string text) => writer.Write(Encoding.ASCII.GetBytes(text));
            Tag("RIFF"); writer.Write(36 + frames * 2); Tag("WAVE"); Tag("fmt "); writer.Write(16);
            writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(48000); writer.Write(96000); writer.Write((ushort)2); writer.Write((ushort)16);
            Tag("data"); writer.Write(frames * 2);
            var pattern = Enumerable.Range(0, 2000).SelectMany(f => BitConverter.GetBytes((short)(f - 1000))).ToArray();
            for (long written = 0; written < frames * 2L; written += pattern.Length)
                writer.Write(pattern, 0, (int)Math.Min(pattern.Length, frames * 2L - written));
        }
        var audio = WaveReader.Read(large);
        Check(audio.IsStreamed && audio.MemoryBytes == 0, "Files above the threshold are streamed, not decoded");
        Equal(audio.Frames, (long)frames); Equal(audio.Overview.Length, frames / AudioBuffer.OverviewBlock);
        foreach (long f in new long[] { 0, 1999, 2000, 12_345_678, frames - 1 }) Equal(audio[f, 0], (f % 2000 - 1000) / 32768f);
        var reader = audio.OpenReader(); double sum = 0, expected = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
        for (long f = 0; f < frames; f++) { sum += reader[f, 0]; expected += (f % 2000 - 1000) / 32768f; }
        Equal(sum, expected);
        Console.WriteLine($"  streamed {new FileInfo(large).Length / 1048576d:F0} MiB WAV · {audio.Duration:F0} s · sequential read {clock.Elapsed.TotalMilliseconds:F0} ms");
        using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Throws<OperationCanceledException>(() => WaveReader.Read(large, cancel.Token)); }
        File.Move(large, large + ".moved"); File.Delete(large + ".moved"); // delete is shared even while the buffer is alive
        Equal(audio[0, 0], -1000 / 32768f); // the cached block holds the end, so this reads the deleted file
    });
    Test("A flat calibration leaves the monitor bit-exact; trims and delays act per speaker and the delay plays out", () =>
    {
        var tone = new AudioBuffer(48000, 1, Enumerable.Range(0, 4800).Select(i => (float)(Math.Sin(i * .05) * .5)).ToArray());
        var t = Track("flat.wav") with { Azimuth = 20, LfeDb = -6 };
        var session = Session(SpeakerLayout.Surround51, t);
        var plain = Drain(new StreamingMixer(session, [new(t, tone)]), 700);
        var flat = Drain(new MonitorChain(new StreamingMixer(session, [new(t, tone)]), SpeakerLayout.Surround51), 700);
        Equal(flat.Length, plain.Length);
        for (int i = 0; i < plain.Length; i++) if (flat[i] != plain[i]) throw new Exception($"Flat calibration changed sample {i}.");
        var impulse = new float[480]; impulse[0] = .5f;
        var centre = Track("c.wav") with { Azimuth = 0 };
        var left = Track("l.wav") with { Azimuth = -30, StartSeconds = 10 / 48000d };
        var calibration = SpeakerCalibration.Flat(SpeakerLayout.Surround51) with
        {
            Speakers = [new("L", 3), new("R"), new("C", -6, 2), new("LFE"), new("Ls"), new("Rs")]
        };
        var mixer = new StreamingMixer(Session(SpeakerLayout.Surround51, centre, left),
            [new(centre, new AudioBuffer(48000, 1, impulse)), new(left, new AudioBuffer(48000, 1, impulse))]);
        var output = Drain(new MonitorChain(mixer, SpeakerLayout.Surround51, calibration), 128);
        Equal(output.Length / 6, 490 + 96); // 2 ms of C delay at 48 kHz play out after the mix ends
        for (int f = 0; f < output.Length / 6; f++)
            for (int c = 0; c < 6; c++)
                Near(output[f * 6 + c], c == 2 && f == 96 ? .5 * Math.Pow(10, -6 / 20d) : c == 0 && f == 10 ? .5 * Math.Pow(10, 3 / 20d) : 0, 1e-9);
        Throws<ArgumentException>(() => new MonitorChain(new StreamingMixer(session, [new(t, tone)]), SpeakerLayout.Surround51, SpeakerCalibration.Flat(SpeakerLayout.Surround71)));
        Throws<ArgumentException>(() => new MonitorChain(new StreamingMixer(session, [new(t, tone)]), SpeakerLayout.Stereo));
        Throws<InvalidDataException>(() => new MonitorChain(Tone(SpeakerLayout.Stereo, 0, 100, .1), SpeakerLayout.Stereo,
            SpeakerCalibration.Flat(SpeakerLayout.Stereo) with { BassManagement = true }));
    });
    Test("Bass management crosses over at 24 dB/oct, sums flat and puts LFE +10 dB on the subwoofer", () =>
    {
        var layout = SpeakerLayout.Surround51;
        var managed = SpeakerCalibration.Flat(layout) with { BassManagement = true, CrossoverHz = 80 };
        double input = Db(.5 / Math.Sqrt(2));
        foreach (var hz in new[] { 30, 80, 300, 2000 })
        {
            var o = Drain(new MonitorChain(Tone(layout, 0, hz, 2), layout, managed), 4800);
            double l = Db(Rms(o, 6, f => o[f * 6])), sub = Db(Rms(o, 6, f => o[f * 6 + 3])), sum = Db(Rms(o, 6, f => o[f * 6] + o[f * 6 + 3]));
            Console.WriteLine($"  {hz,4} Hz on L · L {l - input:F1} dB · sub {sub - input:F1} dB · L+sub {sum - input:F3} dB");
            Near(sum, input, .05);
            for (int c = 1; c < 6; c++) if (c != 3) Check(o.Where((_, i) => i % 6 == c).All(x => x == 0), "Only L and the subwoofer carry L's signal");
            if (hz == 30) Check(l < sub - 25, "30 Hz goes to the subwoofer");
            if (hz == 80) { Near(l, sub, .2); Near(l, input - 6.02, .2); }
            if (hz == 2000) Check(sub < l - 60, "2 kHz stays on L");
        }
        foreach (var (calibration, gain) in new[] { (managed, Math.Pow(10, .5)), (managed with { LfeGain = false }, 1), (SpeakerCalibration.Flat(layout), 1) })
        {
            var source = Tone(layout, 3, 40, .5);
            var reference = Drain(Tone(layout, 3, 40, .5), 480);
            var o = Drain(new MonitorChain(source, layout, calibration), 480);
            for (int f = 0; f < reference.Length / 6; f++) Near(o[f * 6 + 3], reference[f * 6 + 3] * gain, 1e-12);
        }
    });
    Test("Live trim, delay, crossover and bass-management changes ramp without jumps", () =>
    {
        var layout = SpeakerLayout.Surround51;
        var chain = new MonitorChain(Tone(layout, 0, 100, 3), layout);
        var random = new Random(5);
        var block = new double[100 * 6];
        double previous = 0, worst = 0;
        bool managed = false;
        int crossover = 80;
        for (int i = 0; ; i++)
        {
            if (i % 30 == 0) managed = !managed;
            if (i % 45 == 0) crossover = SpeakerCalibration.Crossovers[random.Next(4)];
            chain.Update(SpeakerCalibration.Flat(layout) with
            {
                Speakers = [new("L", -random.Next(13) / 2d, random.Next(201) / 10d), new("R"), new("C"), new("LFE"), new("Ls"), new("Rs")],
                BassManagement = managed, CrossoverHz = crossover
            });
            int frames = chain.Read(block);
            if (frames == 0) break;
            for (int f = 0; f < frames; f++) { worst = Math.Max(worst, Math.Abs(block[f * 6] - previous)); previous = block[f * 6]; }
            if (chain.Finished) break;
        }
        // A 100 Hz sine at −6 dBFS moves at most 0.0065 per sample; an unramped 1 ms delay jump would move about 0.3.
        Console.WriteLine($"  largest step between samples while changing every 100 frames {worst:F4}");
        Check(worst < .012, "Calibration changes must ramp");
    });
    Test("The delay tail follows the calibration in effect when the mix ends", () =>
    {
        var layout = SpeakerLayout.Surround51;
        SpeakerCalibration Delayed(double ms) => SpeakerCalibration.Flat(layout) with { Speakers = [new("L", 0, ms), new("R"), new("C"), new("LFE"), new("Ls"), new("Rs")] };
        // A delay published for the last block still plays out in full.
        var late = new MonitorChain(Tone(layout, 0, 100, 1000 / 48000d), layout);
        var block = new double[2000 * 6];
        Equal(late.Read(block.AsSpan(0, 1000 * 6)), 1000);
        late.Update(Delayed(10));
        Equal(late.Read(block), 480);
        Check(late.Finished, "Finished once the 10 ms tail has been read");
        // Back to flat: once the crossfade is over, the old delay no longer lengthens the end.
        var back = new MonitorChain(Tone(layout, 0, 100, 1), layout, Delayed(10));
        back.Read(new double[4800 * 6]);
        back.Update(SpeakerCalibration.Flat(layout));
        Equal(4800 + Drain(back, 4800).Length / 6, 48000);
    });
    Test("A seek clears delayed audio, so nothing from before the jump is heard", () =>
    {
        var tone = new AudioBuffer(48000, 1, Enumerable.Range(0, 48000).Select(i => (float)(Math.Sin(i * .03) * .5)).ToArray());
        var c = Track("seek.wav") with { Azimuth = 0 };
        var mixer = new StreamingMixer(Session(SpeakerLayout.Surround51, c), [new(c, tone)]);
        var chain = new MonitorChain(mixer, SpeakerLayout.Surround51, SpeakerCalibration.Flat(SpeakerLayout.Surround51) with
        {
            Speakers = [new("L"), new("R"), new("C", 0, 10), new("LFE"), new("Ls"), new("Rs")]
        });
        var block = new double[4800 * 6];
        chain.Read(block); chain.Read(block);
        mixer.Seek(24000);
        Check(chain.SeekPending, "The chain reports the mixer's pending seek");
        chain.Read(block);
        for (int f = 0; f < 480; f++) Equal(block[f * 6 + 2], 0d);
        Check(Enumerable.Range(480, 4320).Any(f => block[f * 6 + 2] != 0), "The new position is heard after the 10 ms delay");
    });
    Test("Test noise plays one speaker at −20 dBFS RMS in its band, stays below −8 dBFS and fades between speakers", () =>
    {
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            var noise = new TestSignal(rate, SpeakerLayout.Surround51) { SpeakerIndex = 0 };
            var block = new double[rate * 6];
            noise.Read(block);
            foreach (var (index, band) in new[] { (0, "mains"), (3, "sub") })
            {
                if (index != noise.SpeakerIndex)
                {
                    noise.SpeakerIndex = index;
                    var turn = new double[rate / 10 * 6];
                    noise.Read(turn);
                    int lastOld = Enumerable.Range(0, rate / 10).Last(f => turn[f * 6] != 0 || f == 0), firstNew = Enumerable.Range(0, rate / 10).First(f => turn[f * 6 + index] != 0);
                    Check(lastOld < firstNew && lastOld <= rate / 50, "The old speaker fades out within 20 ms before the new one starts");
                }
                // Energy outside the band, measured through a 12 dB/oct filter that passes it.
                var outside = Biquad.Design(index == 3 ? EqBandKind.HighPass : EqBandKind.LowPass, new EqBand(true, index == 3 ? 500 : 200), rate);
                double[] h = new double[4];
                double power = 0, leak = 0, peak = 0;
                long n = 0;
                for (int second = 0; second < 5; second++)
                {
                    noise.Read(block);
                    for (int f = 0; f < rate; f++, n++)
                    {
                        for (int c = 0; c < 6; c++) if (c != index && block[f * 6 + c] != 0) throw new Exception($"Noise leaked to channel {c}.");
                        double x = block[f * 6 + index], y = outside.B0 * x + outside.B1 * h[0] + outside.B2 * h[1] - outside.A1 * h[2] - outside.A2 * h[3];
                        h[1] = h[0]; h[0] = x; h[3] = h[2]; h[2] = y;
                        power += x * x; leak += y * y; peak = Math.Max(peak, Math.Abs(x));
                    }
                }
                double level = 10 * Math.Log10(power / n), outsideDb = 10 * Math.Log10(leak / power);
                Console.WriteLine($"  {rate / 1000d:0.#} kHz {band} · {level:F2} dBFS RMS · peak {Db(peak):F1} dBFS · out of band {outsideDb:F1} dB");
                Near(level, TestSignal.LevelDb, .5);
                Check(peak <= TestSignal.PeakLimit, "Noise peaks stay 12 dB above its RMS");
                Check(outsideDb < -15, $"{band} noise stays in its band");
            }
        }
        string Names(SpeakerLayout layout) => string.Join(" ", TestSignal.Order(layout).Select(i => SpatialMixer.Speakers(layout)[i].Name));
        Equal(Names(SpeakerLayout.Surround51), "L C R Rs Ls LFE");
        Equal(Names(SpeakerLayout.Surround714), "L C R Rs Rb Lb Ls Tfl Tfr Tbr Tbl LFE");
        Equal(Names(SpeakerLayout.Stereo), "L R");
    });
    Test("Calibration files round-trip per output and layout, refuse bad values and keep the replaced file", () =>
    {
        var path = Path.Combine(root, "calibration", "calibration.json");
        Equal(CalibrationStore.Load(path).Profiles.Count, 0);
        var five = SpeakerCalibration.Flat(SpeakerLayout.Surround51, "{0.0.0.00000000}.{a}", "Speakers") with
        {
            Speakers = [new("L", -1.5, .25), new("R", 2), new("C", 0, 3.5), new("LFE", -4), new("Ls", 0, 12.3), new("Rs", 10, 50)],
            BassManagement = true, CrossoverHz = 100, LfeGain = false
        };
        var stereo = SpeakerCalibration.Flat(SpeakerLayout.Stereo, five.DeviceId, "Speakers") with { Speakers = [new("L", -20), new("R")] };
        var file = new CalibrationFile().With(five).With(stereo);
        CalibrationStore.Save(path, file);
        var loaded = CalibrationStore.Load(path);
        var back = loaded.Find(five.DeviceId, SpeakerLayout.Surround51)!;
        Check(back with { Speakers = five.Speakers } == five && back.Speakers.SequenceEqual(five.Speakers), "Profiles round-trip");
        Check(loaded.Find(five.DeviceId, SpeakerLayout.Surround71) is null && loaded.Find("other", SpeakerLayout.Stereo) is null, "Profiles are per output and layout");
        Equal(loaded.With(five with { CrossoverHz = 60 }).Profiles.Count, 2);
        var first = File.ReadAllText(path);
        CalibrationStore.Save(path, loaded.With(stereo with { Speakers = [new("L"), new("R", -3)] }));
        Equal(File.ReadAllText(path + ".bak"), first);
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!).All(f => !f.EndsWith(".tmp")), "No temporary file is left behind");
        SpeakerCalibration With(int speaker, SpeakerTrim trim) => five with { Speakers = five.Speakers.Select((s, i) => i == speaker ? trim : s).ToList() };
        foreach (var bad in new[]
        {
            With(0, new("L", double.NaN)), With(0, new("L", 10.5)), With(0, new("L", -20.5)), With(1, new("R", 0, -.1)), With(1, new("R", 0, 50.1)),
            With(2, new("Ls")), five with { CrossoverHz = 90 }, five with { DeviceId = "" }, five with { Speakers = five.Speakers.Take(5).ToList() },
            stereo with { BassManagement = true }, five with { Layout = (SpeakerLayout)42 }
        })
            Throws<InvalidDataException>(() => CalibrationStore.Validate(bad));
        Throws<InvalidDataException>(() => CalibrationStore.Validate(new CalibrationFile { Profiles = [five, five with { CrossoverHz = 60 }] }));
        foreach (var json in new[] { "{", "{\"Version\":2,\"Profiles\":[]}", first.Replace("\"Surround51\"", "\"Quad\""), first.Replace("\"Rs\"", "\"Rb\"") })
        {
            File.WriteAllText(path, json);
            Throws<InvalidDataException>(() => CalibrationStore.Load(path));
        }
        File.WriteAllText(path, new string(' ', 1024 * 1024 + 1));
        Throws<InvalidDataException>(() => CalibrationStore.Load(path));
        // An unreadable file is copied aside before the first save, so two saves (which rotate .bak) cannot lose it.
        var when = new DateTime(2026, 11, 10, 2, 30, 0);
        var aside = CalibrationStore.KeepUnreadable(path, when)!;
        Equal(Path.GetFileName(aside), "calibration.json.unreadable-20261110-023000");
        Equal(new FileInfo(aside).Length, 1024 * 1024 + 1L);
        Equal(Path.GetFileName(CalibrationStore.KeepUnreadable(path, when)!), "calibration.json.unreadable-20261110-023000-2");
        CalibrationStore.Save(path, file); CalibrationStore.Save(path, file);
        Check(File.Exists(aside), "Saving twice keeps the unreadable copy");
        Equal(CalibrationStore.KeepUnreadable(Path.Combine(root, "calibration", "missing.json"), when), null);
        var delays = SpeakerCalibration.DelaysFromDistances([3, 2.5, 3]);
        Check(delays.SequenceEqual([0, 1.46, 0]), $"Delays from distances, got {string.Join(", ", delays)}");
        Equal(SpeakerCalibration.DelaysFromDistances([20, 0]).Max(), SpeakerCalibration.MaxDelayMs); // 58 ms is capped
        Throws<ArgumentOutOfRangeException>(() => SpeakerCalibration.DelaysFromDistances([1, -1]));
    });
}
finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Directory.Delete(root, recursive: true); }
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
LoudnessReport Measure(int rate, SpeakerLayout layout, params (double Seconds, double[] Db)[] parts)
{
    var speakers = SpatialMixer.Speakers(layout);
    var meter = new LoudnessMeter(rate, speakers, measureTruePeak: false);
    var block = new double[4800 * speakers.Length];
    long n = 0;
    foreach (var (seconds, db) in parts)
    {
        var gains = db.Select(d => double.IsNegativeInfinity(d) ? 0 : Math.Pow(10, d / 20)).ToArray();
        long end = n + (long)Math.Round(seconds * rate);
        while (n < end)
        {
            int frames = (int)Math.Min(4800, end - n);
            for (int f = 0; f < frames; f++, n++)
            {
                double s = Math.Sin(2 * Math.PI * 1000 * n / rate);
                for (int c = 0; c < speakers.Length; c++) block[f * speakers.Length + c] = gains[c] * s;
            }
            meter.Process(block.AsSpan(0, frames * speakers.Length));
        }
    }
    return meter.Report();
}
TrackSettings Track(string name) => new() { MediaPath = Path.Combine(root, name), Name = name };
SessionDocument Session(SpeakerLayout layout, params TrackSettings[] tracks) => new() { Layout = layout, MasterGainDb = 0, Tracks = tracks.ToList() };
Dictionary<string, (int Offset, int Size)> Chunks(byte[] bytes)
{
    Check(Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE", "RIFF/WAVE header");
    Check(BitConverter.ToUInt32(bytes, 4) == bytes.Length - 8, "RIFF size must match the file length");
    var chunks = new Dictionary<string, (int, int)>();
    int i = 12;
    while (i + 8 <= bytes.Length)
    {
        string id = Encoding.ASCII.GetString(bytes, i, 4); int size = (int)BitConverter.ToUInt32(bytes, i + 4);
        chunks.Add(id, (i + 8, size));
        i += 8 + size + (size & 1);
    }
    Check(i == bytes.Length, "Chunks must fill the file exactly");
    return chunks;
}
int DataOffset(byte[] bytes) => Chunks(bytes)["data"].Offset;
double Sample24(byte[] bytes, int frame, int channel, int channels)
{
    int i = DataOffset(bytes) + (frame * channels + channel) * 3;
    int sample = bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16;
    return (sample << 8 >> 8) / 8388608d;
}
string Fixture(string name, int channels, int bits, int format, int frames, Func<int, int, double> value, uint mask = 0)
{
    var path = Path.Combine(root, name);
    using var writer = new BinaryWriter(File.Create(path));
    int size = frames * channels * bits / 8;
    void Tag(string text) => writer.Write(Encoding.ASCII.GetBytes(text));
    Tag("RIFF"); writer.Write(36 + (mask != 0 ? 24 : 0) + size + (size & 1)); Tag("WAVE"); Tag("fmt "); writer.Write(mask != 0 ? 40 : 16);
    writer.Write((ushort)(mask != 0 ? 0xfffe : format)); writer.Write((ushort)channels); writer.Write(48000);
    writer.Write(48000 * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
    if (mask != 0)
    {
        writer.Write((ushort)22); writer.Write((ushort)bits); writer.Write(mask);
        writer.Write(new Guid(format == 3 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray());
    }
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
// Reads a source to its end in blocks of the given size.
double[] Drain(IAudioSource source, int frames)
{
    var all = new List<double>();
    var block = new double[frames * source.Channels];
    while (!source.Finished)
    {
        int read = source.Read(block);
        if (read == 0) break;
        all.AddRange(block.AsSpan(0, read * source.Channels).ToArray());
    }
    return all.ToArray();
}
Buffered Tone(SpeakerLayout layout, int channel, double hz, double seconds, int rate = 48000)
{
    int channels = SpatialMixer.Speakers(layout).Length, frames = (int)Math.Round(seconds * rate);
    var samples = new double[frames * channels];
    for (int f = 0; f < frames; f++) samples[f * channels + channel] = .5 * Math.Sin(2 * Math.PI * hz * f / rate);
    return new Buffered(rate, layout, samples);
}
// RMS over the second half, after filters have settled.
double Rms(double[] samples, int channels, Func<int, double> sample)
{
    int frames = samples.Length / channels;
    double power = 0;
    for (int f = frames / 2; f < frames; f++) { double x = sample(f); power += x * x; }
    return Math.Sqrt(power / (frames - frames / 2));
}
double Db(double value) => 20 * Math.Log10(value);

/// <summary>Plays a fixed interleaved buffer, so the monitor chain can be fed exact channel content.</summary>
sealed class Buffered(int rate, SpeakerLayout layout, double[] samples) : IAudioSource
{
    private long frame;
    public int SampleRate => rate;
    public int Channels { get; } = SpatialMixer.Speakers(layout).Length;
    public uint ChannelMask { get; } = SpatialMixer.ChannelMask(layout);
    public bool SeekPending => false;
    public int Seeks => 0;
    public bool Finished => frame * Channels >= samples.Length;
    public int Read(Span<double> destination, CancellationToken cancellationToken = default)
    {
        destination.Clear();
        int frames = (int)Math.Min(destination.Length / Channels, samples.Length / Channels - frame);
        samples.AsSpan((int)(frame * Channels), frames * Channels).CopyTo(destination);
        frame += frames;
        return frames;
    }
}


