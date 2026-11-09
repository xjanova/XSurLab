using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XSurLab.Core;

public enum SampleEncoding { Pcm16, Pcm24, Pcm32, Float32 }

/// <summary>
/// Source audio: decoded floats in memory, or a WAV data chunk streamed from disk in blocks.
/// Multichannel media carries a WAVE channel mask that names each channel.
/// </summary>
public sealed class AudioBuffer
{
    public const int OverviewBlock = 256;
    private readonly float[]? data;
    private readonly WaveData? file;
    private readonly Lock gate = new();
    private AudioReader? shared;
    private float[]? overview;

    public AudioBuffer(int sampleRate, int channels, float[] samples, uint channelMask = 0)
    {
        if (sampleRate <= 0 || channels is < 1 or > 18 || samples.Length % channels != 0)
            throw new ArgumentException("Audio needs a positive rate, 1–18 channels and whole frames.");
        ChannelMask = channelMask == 0 ? SpatialMixer.DefaultMask(channels) : channelMask;
        if (!SpatialMixer.IsValidMask(ChannelMask, channels)) throw new InvalidDataException("The channel mask must name every channel.");
        SampleRate = sampleRate; Channels = channels; data = samples; Frames = samples.Length / channels;
    }
    internal AudioBuffer(WaveData file, float[] peaks)
    {
        SampleRate = file.Rate; Channels = file.Channels; ChannelMask = file.Mask; Frames = file.Frames;
        this.file = file; overview = peaks;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long Frames { get; }
    public uint ChannelMask { get; }
    public double Duration => (double)Frames / SampleRate;
    /// <summary>True when samples are read from the file on demand instead of held in memory.</summary>
    public bool IsStreamed => file is not null;
    /// <summary>Decoded bytes held in memory. Streamed media counts as 0: each reader holds one small block.</summary>
    public long MemoryBytes => data is null ? 0 : data.LongLength * sizeof(float);

    /// <summary>Random access for checks and tools. Real-time and long reads use <see cref="OpenReader"/>, one per thread.</summary>
    public float this[long frame, int channel]
    {
        get
        {
            if (data is not null) return data[frame * Channels + channel];
            lock (gate) return (shared ??= OpenReader())[frame, channel];
        }
    }

    public AudioReader OpenReader() => new(this, data, file);

    /// <summary>Largest magnitude over all channels for each block of <see cref="OverviewBlock"/> frames.</summary>
    public float[] Overview => overview ??= BuildOverview();
    private float[] BuildOverview()
    {
        var peaks = new float[(Frames + OverviewBlock - 1) / OverviewBlock];
        WaveReader.Accumulate(data!, 0, Channels, peaks);
        return peaks;
    }
}

/// <summary>
/// One consumer's view of an <see cref="AudioBuffer"/>. Streamed media keeps the block of frames around the last miss,
/// so sequential reads stay in memory and only a miss reads the disk; a disk failure surfaces as an IOException.
/// Not thread-safe: give each thread its own reader.
/// </summary>
public sealed class AudioReader
{
    internal const int BlockFrames = 16384, Lookback = 1024;
    private readonly float[]? data;
    private readonly WaveData? file;
    private readonly int channels;
    private float[] block = [];
    private byte[] bytes = [];
    private long first, count;
    public long Frames { get; }

    internal AudioReader(AudioBuffer audio, float[]? data, WaveData? file)
    {
        this.data = data; this.file = file; channels = audio.Channels; Frames = audio.Frames;
    }

    public float this[long frame, int channel]
    {
        get
        {
            if (data is not null) return data[frame * channels + channel];
            if ((ulong)(frame - first) >= (ulong)count) Load(frame);
            return block[(frame - first) * channels + channel];
        }
    }
    /// <summary>Sample value, or silence before the first and after the last frame.</summary>
    public float At(long frame, int channel) => frame >= 0 && frame < Frames ? this[frame, channel] : 0;

    // Keeps a little history before the missed frame so a resampler window that straddles the block edge does not reload.
    private void Load(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, Frames);
        if (block.Length == 0) { block = new float[BlockFrames * channels]; bytes = new byte[BlockFrames * channels * file!.Bytes]; }
        count = 0;
        long start = Math.Max(0, frame - Lookback);
        int frames = (int)Math.Min(BlockFrames, Frames - start);
        file!.Read(start, frames, block, bytes);
        first = start; count = frames;
    }
}

/// <summary>An open, read-only handle on a WAV data chunk. Writers are locked out while it is open; deleting is allowed.</summary>
internal sealed class WaveData(SafeFileHandle handle, WaveReader.Header header)
{
    public int Rate => header.Rate;
    public int Channels => header.Channels;
    public uint Mask => header.Mask;
    public int Bytes => header.Bytes;
    public long Frames => header.Frames;

    public void Read(long frame, int frames, Span<float> samples, byte[] scratch)
    {
        int length = frames * Channels * Bytes;
        var span = scratch.AsSpan(0, length);
        long position = header.DataOffset + frame * Channels * Bytes;
        for (int done = 0; done < length;)
        {
            int read = RandomAccess.Read(handle, span[done..], position + done);
            if (read == 0) throw new IOException("The media file ended early. It may have been truncated on disk.");
            done += read;
        }
        WaveReader.Decode(span, header.Encoding, samples[..(frames * Channels)]);
    }

    public void Close() => handle.Dispose();
}

public static class WaveReader
{
    /// <summary>Data chunks larger than this are streamed from disk instead of decoded into memory.</summary>
    public const long StreamThreshold = 8 * 1024 * 1024;
    private const int ScanFrames = 65536;
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");

    /// <param name="stream">true streams from disk, false decodes into memory, null decides by size.</param>
    public static AudioBuffer Read(string path, CancellationToken cancellationToken = default, bool? stream = null)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        WaveData? file = null;
        try
        {
            Header header;
            using (var reader = new BinaryReader(new HandleStream(handle), Encoding.ASCII, leaveOpen: true))
            {
                try { header = ReadHeader(reader, cancellationToken); }
                catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated WAV file.", ex); }
            }
            file = new WaveData(handle, header);
            if (!(stream ?? header.DataSize > StreamThreshold) && header.DataSize / header.Bytes <= Array.MaxLength)
            {
                var samples = new float[header.DataSize / header.Bytes];
                Scan(file, header, samples, null, cancellationToken);
                file.Close();
                return new AudioBuffer(header.Rate, header.Channels, samples, header.Mask);
            }
            var peaks = new float[(header.Frames + AudioBuffer.OverviewBlock - 1) / AudioBuffer.OverviewBlock];
            Scan(file, header, null, peaks, cancellationToken);
            return new AudioBuffer(file, peaks);
        }
        catch
        {
            // A refused or cancelled import must not leave the file locked until the next garbage collection.
            handle.Dispose();
            throw;
        }
    }

    internal sealed record Header(int Rate, int Channels, uint Mask, SampleEncoding Encoding, int Bytes, long DataOffset, long DataSize)
    {
        public long Frames => DataSize / (Bytes * Channels);
    }

    // One pass validates float samples and either fills the decoded array or builds the waveform overview.
    private static void Scan(WaveData file, Header header, float[]? destination, float[]? peaks, CancellationToken cancellationToken)
    {
        int frames = (int)Math.Min(ScanFrames, header.Frames);
        var block = destination is null ? new float[frames * header.Channels] : null;
        var bytes = new byte[frames * header.Channels * header.Bytes];
        for (long start = 0; start < header.Frames; start += ScanFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(ScanFrames, header.Frames - start);
            var samples = destination is null ? block.AsSpan(0, count * header.Channels) : destination.AsSpan((int)(start * header.Channels), count * header.Channels);
            file.Read(start, count, samples, bytes);
            if (header.Encoding == SampleEncoding.Float32)
                foreach (var sample in samples)
                    if (!float.IsFinite(sample) || Math.Abs(sample) > 16)
                        throw new InvalidDataException("Non-finite or excessive float sample (above +24 dBFS).");
            if (peaks is not null) Accumulate(samples, start, header.Channels, peaks);
        }
    }

    internal static void Accumulate(ReadOnlySpan<float> samples, long firstFrame, int channels, float[] peaks)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            long block = (firstFrame + i / channels) / AudioBuffer.OverviewBlock;
            float value = Math.Abs(samples[i]);
            if (value > peaks[block]) peaks[block] = value;
        }
    }

    internal static void Decode(ReadOnlySpan<byte> bytes, SampleEncoding encoding, Span<float> samples)
    {
        switch (encoding)
        {
            case SampleEncoding.Pcm16:
                var shorts = MemoryMarshal.Cast<byte, short>(bytes);
                for (int i = 0; i < samples.Length; i++) samples[i] = shorts[i] / 32768f;
                break;
            case SampleEncoding.Pcm24:
                for (int i = 0, b = 0; i < samples.Length; i++, b += 3)
                    samples[i] = ((bytes[b] | bytes[b + 1] << 8 | bytes[b + 2] << 16) << 8 >> 8) / 8388608f;
                break;
            case SampleEncoding.Pcm32:
                var ints = MemoryMarshal.Cast<byte, int>(bytes);
                for (int i = 0; i < samples.Length; i++) samples[i] = (float)(ints[i] / 2147483648d);
                break;
            default:
                MemoryMarshal.Cast<byte, float>(bytes)[..samples.Length].CopyTo(samples);
                break;
        }
    }

    private static Header ReadHeader(BinaryReader reader, CancellationToken cancellationToken)
    {
        var stream = reader.BaseStream;
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        var container = Tag();
        if (container is not ("RIFF" or "RF64")) throw new InvalidDataException("Only RIFF and RF64 WAV files are supported.");
        long end = reader.ReadUInt32() + 8L, data64 = -1;
        if (Tag() != "WAVE") throw new InvalidDataException("Invalid WAV container.");
        if (container == "RF64")
        {
            // EBU Tech 3306: ds64 comes first and carries the 64-bit RIFF and data sizes.
            uint ds64 = Tag() == "ds64" ? reader.ReadUInt32() : 0;
            if (ds64 < 28) throw new InvalidDataException("RF64 file without a ds64 chunk.");
            long body = stream.Position;
            ulong riff = reader.ReadUInt64(), data = reader.ReadUInt64();
            if (riff > long.MaxValue - 8 || data > long.MaxValue) throw new InvalidDataException("Invalid RF64 sizes.");
            end = (long)riff + 8; data64 = (long)data;
            stream.Position = body + ds64 + (ds64 & 1);
        }
        if (end > stream.Length || end < 12) throw new InvalidDataException("Invalid WAV container length.");
        ushort format = 0, channels = 0, bits = 0, align = 0;
        int rate = 0;
        uint mask = 0;
        long dataOffset = -1, dataSize = 0;
        bool hasFormat = false;
        while (stream.Position + 8 <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = Tag();
            uint size = reader.ReadUInt32();
            // In RF64 the data chunk's 32-bit size reads 0xFFFFFFFF and the real size lives in ds64.
            long length = tag == "data" && size == uint.MaxValue && data64 >= 0 ? data64 : size;
            long next = stream.Position + length;
            if (next > end) throw new InvalidDataException("WAV chunk extends beyond the container.");
            if (tag == "fmt ")
            {
                if (hasFormat || size < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                hasFormat = true;
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                uint byteRate = reader.ReadUInt32();
                align = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (channels is < 1 or > 18) throw new InvalidDataException("Import WAV with 1–18 channels.");
                if (rate is < 8000 or > 192000 || bits is not (16 or 24 or 32) || align != channels * (bits / 8)
                    || byteRate != (uint)(rate * align)) throw new InvalidDataException("Invalid sample format, rate or block alignment.");
                if (format == 0xfffe)
                {
                    if (size < 40) throw new InvalidDataException("Incomplete WAVEFORMATEXTENSIBLE.");
                    var extension = reader.ReadUInt16();
                    var validBits = reader.ReadUInt16();
                    mask = reader.ReadUInt32();
                    var subFormat = new Guid(reader.ReadBytes(16));
                    if (extension < 22 || 18L + extension > size || validBits != bits)
                        throw new InvalidDataException("Unsupported valid-bit depth.");
                    format = subFormat == Pcm ? (ushort)1 : subFormat == Float ? (ushort)3 : (ushort)0;
                }
                if (format != 1 && !(format == 3 && bits == 32))
                    throw new InvalidDataException("Import PCM 16/24/32-bit or IEEE float32 WAV.");
                if (mask == 0) mask = SpatialMixer.DefaultMask(channels);
                if (mask == 0)
                    throw new InvalidDataException($"A {channels}-channel WAV without a channel mask is ambiguous. Save it as WAVE_FORMAT_EXTENSIBLE (7.1 = 0x63F).");
                if (!SpatialMixer.IsValidMask(mask, channels))
                    throw new InvalidDataException($"Channel mask 0x{mask:X} does not name all {channels} channels.");
            }
            else if (tag == "data")
            {
                if (dataOffset >= 0) throw new InvalidDataException("Use a WAV file with a single data chunk.");
                dataOffset = stream.Position;
                dataSize = length;
            }
            stream.Position = next + (length & 1);
            if (stream.Position > end) throw new InvalidDataException("Missing WAV chunk padding.");
        }
        if (!hasFormat || dataOffset < 0 || dataSize == 0 || dataSize % align != 0)
            throw new InvalidDataException("Missing or incomplete audio frames.");
        var encoding = format == 3 ? SampleEncoding.Float32 : bits switch { 16 => SampleEncoding.Pcm16, 24 => SampleEncoding.Pcm24, _ => SampleEncoding.Pcm32 };
        return new Header(rate, channels, mask, encoding, bits / 8, dataOffset, dataSize);
    }

    /// <summary>Seekable read-only view of a file handle that does not own it.</summary>
    private sealed class HandleStream(SafeFileHandle handle) : Stream
    {
        private readonly long length = RandomAccess.GetLength(handle);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(Span<byte> buffer)
        {
            int read = RandomAccess.Read(handle, buffer, Position);
            Position += read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
