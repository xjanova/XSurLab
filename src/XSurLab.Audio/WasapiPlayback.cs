using System.Runtime.InteropServices;
using XSurLab.Core;

namespace XSurLab.Audio;

public sealed record AudioDevice(string Id, string Name, int Channels, int SampleRate, uint ChannelMask, bool IsDefault)
{
    public string DisplayName => $"{Name} · {Channels} ch / {SampleRate / 1000d:0.#} kHz / 0x{ChannelMask:X}{(IsDefault ? " · default" : "")}";
    // Screen readers name list entries by ToString.
    public override string ToString() => DisplayName;
}
/// <summary>Shared goes through the Windows mixer, volume and APOs. Exclusive hands the endpoint the session's samples
/// unchanged at the session rate, and no other app can play on it meanwhile.</summary>
public enum OutputMode { Shared, Exclusive }
public sealed record PlaybackInfo(string DeviceName, int Channels, int SampleRate, double BufferMilliseconds, OutputMode Mode = OutputMode.Shared, string Format = "float32");
/// <summary>The output went away mid-stream: unplugged, disabled, or (shared mode) given a new Windows format.</summary>
public sealed class AudioDeviceLostException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class WasapiPlayback : IDisposable
{
    private const uint EventCallback = 0x00040000;
    private const long SharedBuffer = 500_000; // 50 ms in 100 ns units
    // A running endpoint signals every period (about 10 ms); this long without one means it has stopped.
    private const int EventTimeoutMs = 2000;
    private static readonly EndpointEncoding[] ExclusiveEncodings = [EndpointEncoding.Float32, EndpointEncoding.Int24In32, EndpointEncoding.Int32, EndpointEncoding.Int24, EndpointEncoding.Int16];
    private readonly CancellationTokenSource cancellation = new();
    private double volume;
    private int disposed;
    private long queued;
    public OutputMode Mode { get; }
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

    public WasapiPlayback(StreamingMixer mixer, string deviceId, double volume = .5, IProgress<PlaybackInfo>? started = null, OutputMode mode = OutputMode.Shared)
    {
        Volume = volume;
        Mode = mode;
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
        bool running = false, exclusive = Mode == OutputMode.Exclusive;
        using var ready = new AutoResetEvent(false);
        uint taskIndex = 0;
        IntPtr task = Native.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            token.ThrowIfCancellationRequested();
            Native.Check(enumerator.GetDevice(id, out device));
            string name = FriendlyName(device);
            client = Activate(device);
            uint mask = mixer.StereoMonitor ? 3u : SpatialMixer.ChannelMask(mixer.Layout);
            EndpointEncoding encoding;
            if (exclusive)
            {
                // Exclusive mode bypasses the Windows mix format: the driver itself must accept this exact channel mask and rate.
                encoding = ExclusiveEncoding(client, name, mixer, mask);
                var format = WaveFormat.Create(encoding, mixer.Channels, mixer.SampleRate, mask);
                Native.Check(client.GetDevicePeriod(out long period, out _));
                int result = client.Initialize(1, EventCallback, period, period, ref format, IntPtr.Zero);
                if (result == AudioError.BufferSizeNotAligned)
                {
                    // A rejected client cannot be initialized twice: align the period to the frames it reported, on a fresh client.
                    Native.Check(client.GetBufferSize(out var aligned));
                    Native.Release(client); client = null;
                    client = Activate(device);
                    period = Sample.AlignedPeriod(aligned, mixer.SampleRate);
                    result = client.Initialize(1, EventCallback, period, period, ref format, IntPtr.Zero);
                }
                Native.Check(result);
            }
            else
            {
                encoding = EndpointEncoding.Float32;
                var (channels, _, endpointMask) = MixFormat(client);
                ValidateRouting(mixer.Channels, mask, channels, endpointMask);
                var format = WaveFormat.Float(mixer.Channels, mixer.SampleRate, mask);
                int supported = client.IsFormatSupported(0, ref format, out var closest);
                if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
                if (supported == 1 || supported == AudioError.UnsupportedFormat)
                    throw new NotSupportedException($"{name} cannot play {mixer.Channels} channels at {mixer.SampleRate / 1000d:0.#} kHz. "
                        + "Match the session rate to the endpoint rate, or select another endpoint / Stereo fold-down.");
                Native.Check(supported);
                Native.Check(client.Initialize(0, EventCallback, SharedBuffer, 0, ref format, IntPtr.Zero));
            }
            IAudioClient audio = client;
            Native.Check(audio.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Native.Check(audio.GetBufferSize(out var capacity));
            if (capacity == 0 || capacity > mixer.SampleRate * 2) throw new InvalidOperationException("Unexpected WASAPI buffer size.");
            var iid = typeof(IRenderClient).GUID;
            Native.Check(audio.GetService(ref iid, out var service));
            IRenderClient output = (IRenderClient)service;
            render = output;
            int channelCount = mixer.Channels, frameBytes = Sample.Bytes(encoding) * channelCount;
            double[] samples = new double[checked((int)capacity * channelCount)];
            byte[] bytes = new byte[checked((int)capacity * frameBytes)];
            WaitHandle[] signals = [ready, token.WaitHandle];
            PlaybackInfo info = new(name, channelCount, mixer.SampleRate, capacity * 1000d / mixer.SampleRate, Mode, Sample.Name(encoding));
            bool reported = false;
            if (exclusive) PlayExclusive(); else PlayShared();
            return info;

            void PlayShared()
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (running && mixer.SeekPending) Restart();
                    Native.Check(audio.GetCurrentPadding(out var padding));
                    if (padding > capacity) throw new InvalidOperationException("Invalid WASAPI buffer padding.");
                    Volatile.Write(ref queued, padding);
                    if (mixer.Finished && padding == 0) return;
                    uint available = capacity - padding;
                    int frames = available > 0 && !mixer.Finished ? mixer.Read(samples.AsSpan(0, (int)available * channelCount), token) : 0;
                    // A seek to the very end reads nothing; GetBuffer is never asked for zero frames.
                    if (frames > 0)
                    {
                        Submit(frames);
                        Volatile.Write(ref queued, padding + frames);
                        if (!running) Start();
                    }
                    // A stopped stream does not signal, so it only waits for a moment before reading again.
                    if (running) Await();
                    else if (token.WaitHandle.WaitOne(1)) token.ThrowIfCancellationRequested();
                }
            }
            // Each signal hands the client one whole period to fill; the device plays the previous one meanwhile.
            void PlayExclusive()
            {
                bool tail = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (running && mixer.SeekPending) { Restart(); tail = false; }
                    // Read clears the buffer first, so a short last read is padded with silence.
                    int frames = mixer.Read(samples.AsSpan(0, (int)capacity * channelCount), token);
                    if (frames == 0 && mixer.Finished)
                    {
                        // The last audio is still in the device: one silent period lets it play out before Stop.
                        if (!running || tail) return;
                        tail = true;
                    }
                    Submit((int)capacity);
                    Volatile.Write(ref queued, capacity);
                    if (!running) Start();
                    Await();
                }
            }
            void Submit(int frames)
            {
                int count = frames * channelCount;
                double level = Volume;
                for (int i = 0; i < count; i++)
                {
                    double value = samples[i] * level;
                    if (Math.Abs(value) > 1) throw new InvalidOperationException("Monitor overload. Lower track, master or monitor gain and play again.");
                    samples[i] = value;
                }
                Sample.Encode(samples.AsSpan(0, count), bytes, encoding);
                token.ThrowIfCancellationRequested();
                Native.Check(output.GetBuffer((uint)frames, out var data));
                try { Marshal.Copy(bytes, 0, data, frames * frameBytes); }
                catch { output.ReleaseBuffer((uint)frames, 2); throw; }
                Native.Check(output.ReleaseBuffer((uint)frames, 0));
            }
            void Start()
            {
                Native.Check(audio.Start()); running = true;
                if (!reported) { reported = true; started?.Report(info); }
            }
            // A seek drops what is queued, so the new position is heard at once instead of after the buffer drains.
            void Restart()
            {
                Native.Check(audio.Stop()); running = false;
                Native.Check(audio.Reset());
            }
            void Await()
            {
                int signalled = WaitHandle.WaitAny(signals, EventTimeoutMs);
                if (signalled == 1) token.ThrowIfCancellationRequested();
                if (signalled == WaitHandle.WaitTimeout)
                    throw new AudioDeviceLostException($"{name} stopped asking for audio. Check the device, then refresh outputs.");
            }
        }
        catch (COMException ex) { throw Translate(ex); }
        finally
        {
            if (running) client?.Stop();
            Native.Release(render); Native.Release(client); Native.Release(device); Native.Release(enumerator);
            if (task != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(task);
        }
    }

    private static EndpointEncoding ExclusiveEncoding(IAudioClient client, string name, StreamingMixer mixer, uint mask)
    {
        foreach (var encoding in ExclusiveEncodings)
        {
            var format = WaveFormat.Create(encoding, mixer.Channels, mixer.SampleRate, mask);
            int supported = client.IsFormatSupported(1, ref format, out var closest);
            if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
            if (supported == 0) return encoding;
            if (supported != 1 && supported != AudioError.UnsupportedFormat) Native.Check(supported);
        }
        throw new NotSupportedException($"{name} does not accept {mixer.Channels} channels / mask 0x{mask:X} at {mixer.SampleRate / 1000d:0.#} kHz in exclusive mode "
            + "(tried float32, PCM 24-in-32, 32, 24 and 16). Choose Shared, or a layout and rate the device plays natively.");
    }

    internal static Exception Translate(COMException ex) => ex.HResult switch
    {
        AudioError.DeviceInvalidated or AudioError.ResourcesInvalidated
            => new AudioDeviceLostException("The output was removed, disabled or given a new Windows format · playback stopped.", ex),
        AudioError.DeviceInUse => new IOException("Another app holds this output in exclusive mode. Close it, or choose another output.", ex),
        AudioError.ExclusiveNotAllowed => new IOException("Windows does not allow exclusive mode on this output. Turn on "
            + "\"Allow applications to take exclusive control\" in Sound settings › device properties › Advanced, or choose Shared.", ex),
        AudioError.ServiceNotRunning => new IOException("The Windows Audio service is not running.", ex),
        _ => new IOException($"WASAPI stopped (0x{ex.HResult:X8}). Check the selected device, its Windows format and any exclusive-mode app, then refresh outputs.", ex)
    };

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
