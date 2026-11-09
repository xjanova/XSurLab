using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

Check("Exclusive formats pack little-endian and hold full scale", () =>
{
    Expect(EndpointEncoding.Int16, [1, -1, .5], [0xFF, 0x7F, 0x00, 0x80, 0x00, 0x40]);
    Expect(EndpointEncoding.Int24, [1, -1, -.5], [0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80, 0x00, 0x00, 0xC0]);
    Expect(EndpointEncoding.Int24In32, [.5, -1], [0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x80]);
    Expect(EndpointEncoding.Int32, [1, -1], [0xFF, 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x00, 0x80]);
    Expect(EndpointEncoding.Float32, [.25], BitConverter.GetBytes(.25f));
    // One step below full scale stays distinct from it: rounding, not truncation.
    Expect(EndpointEncoding.Int16, [-1 + 1d / 32768, 1d / 65536 * 1.01], [0x01, 0x80, 0x01, 0x00]);
    static void Expect(EndpointEncoding encoding, double[] source, byte[] expected)
    {
        var actual = new byte[expected.Length];
        Sample.Encode(source, actual, encoding);
        if (!actual.SequenceEqual(expected))
            throw new Exception($"{encoding}: {Convert.ToHexString(actual)} instead of {Convert.ToHexString(expected)}.");
    }
});
Check("Exclusive WAVEFORMATEXTENSIBLE describes each container", () =>
{
    if (Marshal.SizeOf<WaveFormat>() != 40) throw new Exception("WAVEFORMATEXTENSIBLE must be 40 bytes.");
    var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
    Same(WaveFormat.Create(EndpointEncoding.Int24In32, 8, 48000, 0x63f), 32, 24, 32, pcm);
    Same(WaveFormat.Create(EndpointEncoding.Int24, 6, 48000, 0x60f), 24, 24, 18, pcm);
    Same(WaveFormat.Create(EndpointEncoding.Int16, 2, 44100, 3), 16, 16, 4, pcm);
    Same(WaveFormat.Float(12, 48000, 0x2d63f), 32, 32, 48, new Guid("00000003-0000-0010-8000-00aa00389b71"));
    static void Same(WaveFormat format, int bits, int valid, int block, Guid subFormat)
    {
        if (format.Bits != bits || format.ValidBits != valid || format.BlockAlign != block || format.SubFormat != subFormat
            || format.BytesPerSecond != format.Rate * block || format.Tag != 0xfffe || format.Extra != 22)
            throw new Exception($"Unexpected format {format.Bits}/{format.ValidBits} bits, block {format.BlockAlign}.");
    }
});
Check("Exclusive periods align to whole frames", () =>
{
    if (Sample.AlignedPeriod(480, 48000) != 100_000 || Sample.AlignedPeriod(441, 44100) != 100_000 || Sample.AlignedPeriod(512, 48000) != 106_667)
        throw new Exception("Aligned period is not 10^7 × frames / rate rounded.");
});
Check("Output list updates in place and keeps unchanged entries", () =>
{
    AudioDevice a = new("a", "Speakers", 2, 48000, 3, true), b = new("b", "HDMI", 8, 48000, 0x63f, false), c = new("c", "USB", 2, 44100, 3, false);
    var list = new ObservableCollection<AudioDevice> { a, b, c };
    int resets = 0;
    var removedItems = new List<object>();
    list.CollectionChanged += (_, e) =>
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        if (e.OldItems is not null && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove) removedItems.AddRange(e.OldItems.Cast<object>());
    };
    // b is unplugged, d is plugged in, c becomes the default (its record changes), a stays as it is.
    AudioDevice d = new("d", "Headphones", 2, 48000, 3, false), c2 = c with { IsDefault = true }, a2 = a with { IsDefault = false };
    IReadOnlyList<AudioDevice> next = [c2, a2, d];
    DeviceList.Sync(list, next);
    if (!list.SequenceEqual(next)) throw new Exception("List does not match the enumeration.");
    if (resets != 0) throw new Exception("List was cleared instead of updated.");
    DeviceList.Sync(list, [c2, d, a2]);
    if (!list.SequenceEqual([c2, d, a2]) || removedItems.Contains(d) || removedItems.Contains(c2)) throw new Exception("A reorder removed an unchanged entry.");
    var random = new Random(7);
    for (int round = 0; round < 200; round++)
    {
        var pool = Enumerable.Range(0, 6).Select(i => new AudioDevice($"id{i}", $"D{i}", 2, 48000, 3, random.Next(2) == 0)).ToArray();
        var target = new ObservableCollection<AudioDevice>(pool.Where(_ => random.Next(2) == 0).OrderBy(_ => random.Next()));
        var wanted = pool.Where(_ => random.Next(2) == 0).OrderBy(_ => random.Next()).ToArray();
        DeviceList.Sync(target, wanted);
        if (!target.SequenceEqual(wanted)) throw new Exception($"Random round {round} did not converge.");
    }
});
Check("Device notifications arrive through the COM vtable", () =>
{
    if (!Environment.Is64BitProcess) { Console.WriteLine("GAP Notification ABI check is written for 64-bit processes."); return; }
    var seen = new List<(DeviceChangeKind Kind, string? Id)>();
    var client = new NotificationClient((kind, id) => seen.Add((kind, id)));
    IntPtr unknown = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
    IntPtr key = Marshal.AllocHGlobal(Marshal.SizeOf<PropertyKey>());
    try
    {
        // Slots after IUnknown: 3 state, 4 added, 5 removed, 6 default, 7 property. A 20-byte PROPERTYKEY travels by pointer on x64 and ARM64.
        IntPtr table = Marshal.ReadIntPtr(unknown);
        T Slot<T>(int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
        Slot<StateCall>(3)(unknown, "s", 1);
        Slot<IdCall>(4)(unknown, "plugged");
        Slot<IdCall>(5)(unknown, "unplugged");
        Slot<DefaultCall>(6)(unknown, 0, 1, "new-default");
        Slot<DefaultCall>(6)(unknown, 1, 1, "capture-default");
        Slot<DefaultCall>(6)(unknown, 0, 0, "console-role");
        Marshal.StructureToPtr(new PropertyKey { Format = NotificationClient.FormatKey, Id = 0 }, key, false);
        Slot<PropertyCall>(7)(unknown, "reformatted", key);
        Marshal.StructureToPtr(new PropertyKey { Format = Guid.NewGuid(), Id = 3 }, key, false);
        Slot<PropertyCall>(7)(unknown, "noise", key);
    }
    finally { Marshal.FreeHGlobal(key); Marshal.Release(unknown); }
    (DeviceChangeKind, string?)[] expected = [(DeviceChangeKind.State, "s"), (DeviceChangeKind.Added, "plugged"), (DeviceChangeKind.Removed, "unplugged"),
        (DeviceChangeKind.Default, "new-default"), (DeviceChangeKind.Format, "reformatted")];
    if (!seen.SequenceEqual(expected)) throw new Exception("Notifications: " + string.Join(", ", seen));
});

if (args.Contains("--devices"))
{
    try
    {
        using (new DeviceWatcher()) Console.WriteLine("PASS Device notifications register and unregister.");
        var devices = await WasapiPlayback.EnumerateAsync();
        Console.WriteLine($"WASAPI: {devices.Count} active output(s).");
        var surround = new Dictionary<OutputMode, int> { [OutputMode.Shared] = 0, [OutputMode.Exclusive] = 0 };
        foreach (var device in devices)
        {
            Console.WriteLine($"DEVICE {device.DisplayName} / mask 0x{device.ChannelMask:X}");
            if (device.SampleRate is not (44100 or 48000 or 96000))
            { Console.WriteLine("GAP Unsupported session rate for device check."); continue; }
            foreach (var mode in Enum.GetValues<OutputMode>())
            {
                bool available = true;
                foreach (var layout in Enum.GetValues<SpeakerLayout>())
                {
                    int channels = SpatialMixer.Speakers(layout).Length;
                    if (mode == OutputMode.Shared && channels > 2 && (channels != device.Channels || SpatialMixer.ChannelMask(layout) != device.ChannelMask))
                    { Console.WriteLine($"GAP Shared {layout}: device layout does not match."); continue; }
                    available = await Hardware(mode, $"{mode} {layout}", async () =>
                    {
                        var mixer = Silent(layout, device.SampleRate, device.SampleRate / 10);
                        using var playback = new WasapiPlayback(mixer, device.Id, mode: mode);
                        var info = await playback.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                        if (mixer.Position != mixer.TotalFrames) throw new Exception("Playback did not drain the entire stream.");
                        if (channels > 2) surround[mode]++;
                        Console.WriteLine($"PASS Silent WASAPI {mode} playback/drain {layout} / {info.Channels} ch / {info.Format} / {info.BufferMilliseconds:F1} ms {(mode == OutputMode.Exclusive ? "period" : "buffer")}.");
                    });
                    if (!available) break;
                }
                if (!available) continue;
                await Hardware(mode, $"{mode} seek", async () =>
                {
                    // Two seconds of silence; jumping to the last 100 ms must finish far sooner than playing it all.
                    var mixer = Silent(SpeakerLayout.Stereo, device.SampleRate, device.SampleRate * 2);
                    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var playback = new WasapiPlayback(mixer, device.Id, started: new Started(_ => started.TrySetResult()), mode: mode);
                    await Task.WhenAny(started.Task, playback.Completion).WaitAsync(TimeSpan.FromSeconds(10));
                    var clock = Stopwatch.StartNew();
                    mixer.Seek(mixer.TotalFrames - device.SampleRate / 10);
                    await playback.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                    if (mixer.Position != mixer.TotalFrames) throw new Exception("Playback did not reach the end after the seek.");
                    if (clock.Elapsed > TimeSpan.FromSeconds(1)) throw new Exception($"Seek was not heard at once: finished after {clock.Elapsed.TotalMilliseconds:F0} ms.");
                    Console.WriteLine($"PASS WASAPI {mode} seek drops the queue ({clock.Elapsed.TotalMilliseconds:F0} ms to the end).");
                });
                await Hardware(mode, $"{mode} cancel", async () =>
                {
                    var mixer = Silent(SpeakerLayout.Stereo, device.SampleRate, device.SampleRate * 3);
                    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var playback = new WasapiPlayback(mixer, device.Id, started: new Started(_ => started.TrySetResult()), mode: mode);
                    await Task.WhenAny(started.Task, playback.Completion).WaitAsync(TimeSpan.FromSeconds(10));
                    playback.Dispose();
                    try { await playback.Completion.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancellation did not stop playback."); }
                    catch (TaskCanceledException) { Console.WriteLine($"PASS WASAPI {mode} cancellation/release."); }
                });
                await Hardware(mode, $"{mode} calibrated", async () =>
                {
                    // A 50 ms delay on L plays out after the mix ends: Finished only turns true once that tail has been read.
                    var mixer = Silent(SpeakerLayout.Stereo, device.SampleRate, device.SampleRate / 10);
                    var chain = new MonitorChain(mixer, SpeakerLayout.Stereo, SpeakerCalibration.Flat(SpeakerLayout.Stereo) with
                    {
                        Speakers = [new("L", -3, SpeakerCalibration.MaxDelayMs), new("R")]
                    });
                    using (var playback = new WasapiPlayback(chain, device.Id, mode: mode))
                    {
                        await playback.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                        if (mixer.Position != mixer.TotalFrames || !chain.Finished) throw new Exception("Calibrated playback did not play out its delay.");
                    }
                    // The test signal never ends by itself; with no speaker chosen it is silent and stops when cancelled.
                    var noise = new TestSignal(device.SampleRate, SpeakerLayout.Stereo);
                    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var test = new WasapiPlayback(new MonitorChain(noise, SpeakerLayout.Stereo, bypassBassManagement: true), device.Id,
                        started: new Started(_ => started.TrySetResult()), mode: mode);
                    await Task.WhenAny(started.Task, test.Completion).WaitAsync(TimeSpan.FromSeconds(10));
                    test.Dispose();
                    try { await test.Completion.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("The test signal did not stop when cancelled."); }
                    catch (TaskCanceledException) { Console.WriteLine($"PASS WASAPI {mode} calibrated drain and silent test signal."); }
                });
            }
        }
        if (devices.Count == 0) Console.WriteLine("GAP No audio endpoint: hardware playback not checked.");
        foreach (var (mode, count) in surround)
            if (count == 0) Console.WriteLine($"GAP No {mode} surround output: discrete surround hardware and speaker wiring not checked in this mode.");
    }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL WASAPI device check: {ex}"); }
}
else Console.WriteLine("GAP Hardware playback not requested; use --devices for silent device checks.");
Environment.ExitCode = failures == 0 ? 0 : 1;

// Exclusive mode can be switched off in Windows, held by another app, or not offered for a layout: those are gaps, not failures.
// Returns false when the mode is unavailable on this output, so the remaining checks for it are skipped.
async Task<bool> Hardware(OutputMode mode, string label, Func<Task> body)
{
    try { await body(); return true; }
    catch (NotSupportedException ex) when (mode == OutputMode.Exclusive) { Console.WriteLine($"GAP {label}: {ex.Message}"); return true; }
    catch (IOException ex) when (mode == OutputMode.Exclusive && ex.InnerException is COMException { HResult: unchecked((int)0x8889000E) or unchecked((int)0x8889000A) })
    { Console.WriteLine($"GAP {label}: {ex.Message}"); return false; }
}
static StreamingMixer Silent(SpeakerLayout layout, int rate, int frames)
{
    var track = new TrackSettings { Name = "Silent device check", MediaPath = "in-memory-silence.wav" };
    var session = new SessionDocument { Layout = layout, SampleRate = rate, Tracks = [track] };
    return new StreamingMixer(session, [new(track, new AudioBuffer(rate, 1, new float[frames]))]);
}
void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
sealed class Started(Action<PlaybackInfo> action) : IProgress<PlaybackInfo>
{
    public void Report(PlaybackInfo value) => action(value);
}
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StateCall(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int IdCall(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DefaultCall(IntPtr self, int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PropertyCall(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key);
