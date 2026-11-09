using System.Runtime.InteropServices;
using XSurLab.Core;

namespace XSurLab.Audio;

public sealed record AudioDevice(string Id, string Name, int Channels, int SampleRate, uint ChannelMask, bool IsDefault)
{
    public string DisplayName => $"{Name} · {Channels} ch / {SampleRate / 1000d:0.#} kHz / 0x{ChannelMask:X}{(IsDefault ? " · default" : "")}";
}
public sealed record PlaybackInfo(string DeviceName, int Channels, int SampleRate, double BufferMilliseconds);

public sealed class WasapiPlayback : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private double volume;
    private int disposed;
    private long queued;
    public Task<PlaybackInfo> Completion { get; }
    /// <summary>Frames read from the mixer but not yet heard; the audible frame is mixer.Position minus this.</summary>
    public long QueuedFrames => Volatile.Read(ref queued);
    public double Volume
    {
        get => Volatile.Read(ref volume);
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref volume, value);
        }
    }

    public WasapiPlayback(StreamingMixer mixer, string deviceId, double volume = .5, IProgress<PlaybackInfo>? started = null)
    {
        Volume = volume;
        Completion = OnAudioThread(() =>
        {
            try { return Render(mixer, deviceId, started, cancellation.Token); }
            finally { cancellation.Dispose(); }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public static Task<IReadOnlyList<AudioDevice>> EnumerateAsync() => OnAudioThread<IReadOnlyList<AudioDevice>>(() =>
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumeratorObject();
        IDeviceCollection? collection = null;
        IDevice? defaultDevice = null;
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) >= 0)
            { Native.Check(defaultDevice.GetId(out var id)); defaultId = id; }
            Native.Check(enumerator.EnumAudioEndpoints(0, 1, out collection));
            Native.Check(collection.GetCount(out var count));
            List<AudioDevice> devices = [];
            for (uint i = 0; i < count; i++)
            {
                Native.Check(collection.Item(i, out var device));
                IAudioClient? client = null;
                try
                {
                    Native.Check(device.GetId(out var id));
                    string name = FriendlyName(device);
                    client = Activate(device);
                    var (channels, rate, mask) = MixFormat(client);
                    devices.Add(new(id, name, channels, rate, mask, id == defaultId));
                }
                catch (COMException) { /* A disconnected endpoint may remain in this enumeration snapshot. */ }
                finally { Native.Release(client); Native.Release(device); }
            }
            return devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToArray();
        }
        finally { Native.Release(defaultDevice); Native.Release(collection); Native.Release(enumerator); }
    });

    public static void ValidateRouting(int outputChannels, uint outputMask, int endpointChannels, uint endpointMask)
    {
        if (outputChannels > 2 && (outputChannels != endpointChannels || outputMask != endpointMask))
            throw new NotSupportedException($"Discrete surround needs a Windows endpoint configured as {outputChannels} channels / mask 0x{outputMask:X}. "
                + $"This endpoint reports {endpointChannels} channels / mask 0x{endpointMask:X}. Select another endpoint or enable Stereo fold-down.");
    }

    private PlaybackInfo Render(StreamingMixer mixer, string id, IProgress<PlaybackInfo>? started, CancellationToken token)
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumeratorObject();
        IDevice? device = null;
        IAudioClient? client = null;
        IRenderClient? render = null;
        bool running = false;
        try
        {
            token.ThrowIfCancellationRequested();
            Native.Check(enumerator.GetDevice(id, out device));
            string name = FriendlyName(device);
            client = Activate(device);
            var (channels, _, mask) = MixFormat(client);
            uint desiredMask = mixer.StereoMonitor ? 3u : SpatialMixer.ChannelMask(mixer.Layout);
            ValidateRouting(mixer.Channels, desiredMask, channels, mask);
            var format = WaveFormat.Float(mixer.Channels, mixer.SampleRate, desiredMask);
            int supported = client.IsFormatSupported(0, ref format, out var closest);
            if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
            if (supported == 1 || supported == unchecked((int)0x88890008))
                throw new NotSupportedException($"{name} cannot play {mixer.Channels} channels at {mixer.SampleRate / 1000d:0.#} kHz. "
                    + "Match the session rate to the endpoint rate, or select another endpoint / Stereo fold-down.");
            Native.Check(supported);
            Native.Check(client.Initialize(0, 0, 500000, 0, ref format, IntPtr.Zero));
            Native.Check(client.GetBufferSize(out var capacity));
            if (capacity == 0 || capacity > mixer.SampleRate * 2) throw new InvalidOperationException("Unexpected WASAPI buffer size.");
            var iid = typeof(IRenderClient).GUID;
            Native.Check(client.GetService(ref iid, out var service));
            render = (IRenderClient)service;
            var samples = new double[checked((int)capacity * mixer.Channels)];
            var floats = new float[samples.Length];
            var info = new PlaybackInfo(name, mixer.Channels, mixer.SampleRate, capacity * 1000d / mixer.SampleRate);
            bool reported = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // A seek drops what is queued, so the new position is heard at once instead of after the buffer drains.
                if (running && mixer.SeekPending)
                {
                    Native.Check(client.Stop()); running = false;
                    Native.Check(client.Reset());
                }
                Native.Check(client.GetCurrentPadding(out var padding));
                if (padding > capacity) throw new InvalidOperationException("Invalid WASAPI buffer padding.");
                Volatile.Write(ref queued, padding);
                if (mixer.Finished && padding == 0) break;
                uint available = capacity - padding;
                int frames = available > 0 && !mixer.Finished ? mixer.Read(samples.AsSpan(0, (int)available * mixer.Channels), token) : 0;
                // A seek to the very end reads nothing; GetBuffer is never asked for zero frames.
                if (frames > 0)
                {
                    int count = frames * mixer.Channels;
                    double level = Volume;
                    for (int i = 0; i < count; i++)
                    {
                        double value = samples[i] * level;
                        if (Math.Abs(value) > 1) throw new InvalidOperationException("Monitor overload. Lower track, master or monitor gain and play again.");
                        floats[i] = (float)value;
                    }
                    token.ThrowIfCancellationRequested();
                    Native.Check(render.GetBuffer((uint)frames, out var data));
                    try { Marshal.Copy(floats, 0, data, count); }
                    catch { render.ReleaseBuffer((uint)frames, 2); throw; }
                    Native.Check(render.ReleaseBuffer((uint)frames, 0));
                    Volatile.Write(ref queued, padding + frames);
                    if (!running)
                    {
                        Native.Check(client.Start()); running = true;
                        if (!reported) { reported = true; started?.Report(info); }
                    }
                }
                if (token.WaitHandle.WaitOne(5)) token.ThrowIfCancellationRequested();
            }
            return info;
        }
        catch (COMException ex)
        {
            throw new IOException($"WASAPI stopped (0x{ex.HResult:X8}). Check the selected device, its Windows format and any exclusive-mode app, then refresh outputs.", ex);
        }
        finally
        {
            if (running) client?.Stop();
            Native.Release(render); Native.Release(client); Native.Release(device); Native.Release(enumerator);
        }
    }

    private static IAudioClient Activate(IDevice device)
    {
        var iid = typeof(IAudioClient).GUID;
        Native.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var instance));
        return (IAudioClient)instance;
    }
    private static (int Channels, int Rate, uint Mask) MixFormat(IAudioClient client)
    {
        Native.Check(client.GetMixFormat(out var pointer));
        try
        {
            int channels = (ushort)Marshal.ReadInt16(pointer, 2), rate = Marshal.ReadInt32(pointer, 4);
            uint mask = (ushort)Marshal.ReadInt16(pointer) == 0xfffe && (ushort)Marshal.ReadInt16(pointer, 16) >= 22
                ? unchecked((uint)Marshal.ReadInt32(pointer, 20)) : channels == 2 ? 3u : 0u;
            return (channels, rate, mask);
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    private static string FriendlyName(IDevice device)
    {
        Native.Check(device.OpenPropertyStore(0, out var store));
        try
        {
            var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            Native.Check(store.GetValue(ref key, out var value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "Audio output" : "Audio output"; }
            finally { Native.PropVariantClear(ref value); }
        }
        finally { Native.Release(store); }
    }
    private static Task<T> OnAudioThread<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            bool initialized = false;
            try
            {
                Native.Check(Native.CoInitializeEx(IntPtr.Zero, 2)); initialized = true;
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { if (initialized) Native.CoUninitialize(); }
        }) { IsBackground = true, Name = "XSurLab WASAPI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
