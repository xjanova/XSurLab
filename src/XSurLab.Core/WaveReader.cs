using System.Text;

namespace XSurLab.Core;

public sealed class AudioBuffer(int sampleRate, int channels, float[] samples)
{
    private readonly float[] data = samples;
    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public ReadOnlyMemory<float> Samples => data;
    public int Frames => data.Length / Channels;
    public double Duration => (double)Frames / SampleRate;

    public float this[long frame, int channel] => data[frame * Channels + channel];
    /// <summary>Sample value, or silence before the first and after the last frame.</summary>
    public float At(long frame, int channel) => frame >= 0 && frame < Frames ? data[frame * Channels + channel] : 0;
}

public static class WaveReader
{
    public const int MaxDataBytes = 64 * 1024 * 1024;
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");

    public static AudioBuffer Read(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        try { return Read(reader, cancellationToken); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated WAV file.", ex); }
    }

    private static AudioBuffer Read(BinaryReader reader, CancellationToken cancellationToken)
    {
        var stream = reader.BaseStream;
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (Tag() != "RIFF") throw new InvalidDataException("Only RIFF WAV files are supported.");
        long end = reader.ReadUInt32() + 8L;
        if (Tag() != "WAVE" || end > stream.Length || end < 12)
            throw new InvalidDataException("Invalid WAV container length.");
        ushort format = 0, channels = 0, bits = 0, align = 0;
        int rate = 0;
        long dataOffset = -1;
        int dataSize = 0;
        bool hasFormat = false;
        while (stream.Position + 8 <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = Tag();
            uint size = reader.ReadUInt32();
            long next = stream.Position + size;
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
                if (channels is not (1 or 2)) throw new InvalidDataException("Import mono or stereo WAV. Multichannel source import is not supported yet.");
                if (rate is < 8000 or > 192000 || bits is not (16 or 24 or 32) || align != channels * (bits / 8)
                    || byteRate != (uint)(rate * align)) throw new InvalidDataException("Invalid sample format, rate or block alignment.");
                if (format == 0xfffe)
                {
                    if (size < 40) throw new InvalidDataException("Incomplete WAVEFORMATEXTENSIBLE.");
                    var extension = reader.ReadUInt16();
                    var validBits = reader.ReadUInt16();
                    var mask = reader.ReadUInt32();
                    var subFormat = new Guid(reader.ReadBytes(16));
                    if (extension < 22 || 18L + extension > size || validBits != bits)
                        throw new InvalidDataException("Unsupported valid-bit depth.");
                    if (mask != 0 && mask != (channels == 1 ? 4u : 3u))
                        throw new InvalidDataException("Source channel mask must identify mono center or stereo L/R.");
                    format = subFormat == Pcm ? (ushort)1 : subFormat == Float ? (ushort)3 : (ushort)0;
                }
                if (format != 1 && !(format == 3 && bits == 32))
                    throw new InvalidDataException("Import PCM 16/24/32-bit or IEEE float32 WAV.");
            }
            else if (tag == "data")
            {
                if (dataOffset >= 0 || size > MaxDataBytes) throw new InvalidDataException("Use a single WAV data chunk of at most 64 MiB.");
                dataOffset = stream.Position;
                dataSize = (int)size;
            }
            stream.Position = next + (size & 1);
            if (stream.Position > end) throw new InvalidDataException("Missing WAV chunk padding.");
        }
        if (!hasFormat || dataOffset < 0 || dataSize == 0 || dataSize % align != 0)
            throw new InvalidDataException("Missing or incomplete audio frames.");
        stream.Position = dataOffset;
        var samples = new float[dataSize / (bits / 8)];
        for (int i = 0; i < samples.Length; i++)
        {
            if (i % 16384 == 0) cancellationToken.ThrowIfCancellationRequested();
            float sample;
            if (format == 3) sample = reader.ReadSingle();
            else if (bits == 16) sample = reader.ReadInt16() / 32768f;
            else if (bits == 24)
            {
                int value = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
                sample = (value << 8 >> 8) / 8388608f;
            }
            else sample = (float)(reader.ReadInt32() / 2147483648d);
            if (!float.IsFinite(sample) || Math.Abs(sample) > 16)
                throw new InvalidDataException("Non-finite or excessive float sample (above +24 dBFS).");
            samples[i] = sample;
        }
        return new AudioBuffer(rate, channels, samples);
    }
}

