using XSurLab.Audio;
using XSurLab.Core;

int failures = 0;
Check("Accept exact discrete 5.1 and 7.1 routing", () =>
{
    WasapiPlayback.ValidateRouting(6, 0x60f, 6, 0x60f);
    WasapiPlayback.ValidateRouting(6, 0x3f, 6, 0x3f);
    WasapiPlayback.ValidateRouting(8, 0x63f, 8, 0x63f);
});
Check("Reject virtual surround, wrong mask and wrong channel count", () =>
{
    Reject(6, 0x60f, 2, 3); Reject(6, 0x60f, 6, 0x3f); Reject(6, 0x60f, 8, 0x63f);
    Reject(8, 0x63f, 8, 0); Reject(8, 0x63f, 6, 0x60f);
    void Reject(int channels, uint mask, int deviceChannels, uint deviceMask)
    {
        try { WasapiPlayback.ValidateRouting(channels, mask, deviceChannels, deviceMask); }
        catch (NotSupportedException) { return; }
        throw new Exception("Mismatched surround configuration was accepted.");
    }
});
Check("Stereo fold-down may use a multichannel endpoint", () => WasapiPlayback.ValidateRouting(2, 3, 8, 0x63f));

if (args.Contains("--devices"))
{
    try
    {
        var devices = await WasapiPlayback.EnumerateAsync();
        Console.WriteLine($"WASAPI: {devices.Count} active output(s).");
        int supportedSurround = 0;
        foreach (var device in devices)
        {
            Console.WriteLine($"DEVICE {device.DisplayName} / mask 0x{device.ChannelMask:X}");
            if (device.SampleRate is not (44100 or 48000 or 96000))
            { Console.WriteLine("GAP Unsupported session rate for device check."); continue; }
            foreach (var layout in Enum.GetValues<SpeakerLayout>())
            {
                int channels = SpatialMixer.Speakers(layout).Length;
                if (channels > 2 && (channels != device.Channels || SpatialMixer.ChannelMask(layout) != device.ChannelMask))
                { Console.WriteLine($"GAP {layout}: device layout does not match."); continue; }
                var track = new TrackSettings { Name = "Silent device check", MediaPath = "in-memory-silence.wav" };
                var session = new SessionDocument { Layout = layout, SampleRate = device.SampleRate, Tracks = [track] };
                var mixer = new StreamingMixer(session, [new(track, new AudioBuffer(device.SampleRate, 1, new float[device.SampleRate / 10]))]);
                using var playback = new WasapiPlayback(mixer, device.Id);
                var info = await playback.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                if (mixer.Position != mixer.TotalFrames) throw new Exception("Playback did not drain the entire stream.");
                if (channels > 2) supportedSurround++;
                Console.WriteLine($"PASS Silent WASAPI playback/drain {layout} / {info.Channels} ch / {info.BufferMilliseconds:F1} ms buffer.");
            }
            var longTrack = new TrackSettings { Name = "Cancel check", MediaPath = "in-memory-cancel.wav" };
            var longSession = new SessionDocument { Layout = SpeakerLayout.Stereo, SampleRate = device.SampleRate, Tracks = [longTrack] };
            var longMixer = new StreamingMixer(longSession, [new(longTrack, new AudioBuffer(device.SampleRate, 1, new float[device.SampleRate * 3]))]);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelPlayback = new WasapiPlayback(longMixer, device.Id, started: new Started(_ => started.TrySetResult()));
            await Task.WhenAny(started.Task, cancelPlayback.Completion).WaitAsync(TimeSpan.FromSeconds(10));
            cancelPlayback.Dispose();
            try { await cancelPlayback.Completion.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancellation did not stop playback."); }
            catch (TaskCanceledException) { Console.WriteLine("PASS WASAPI cancellation/release."); }
        }
        if (devices.Count == 0) Console.WriteLine("GAP No audio endpoint: hardware playback not checked.");
        if (supportedSurround == 0) Console.WriteLine("GAP No matching surround endpoint: discrete surround hardware and speaker wiring not checked.");
    }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL WASAPI device check: {ex}"); }
}
else Console.WriteLine("GAP Hardware playback not requested; use --devices for silent device checks.");
Environment.ExitCode = failures == 0 ? 0 : 1;
void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
sealed class Started(Action<PlaybackInfo> action) : IProgress<PlaybackInfo>
{
    public void Report(PlaybackInfo value) => action(value);
}
