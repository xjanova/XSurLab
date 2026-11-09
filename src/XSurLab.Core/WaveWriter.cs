using System.Text;

namespace XSurLab.Core;

public enum SampleFormat { Pcm24, Pcm16, Float32 }

public sealed record ExportOptions
{
    public SampleFormat Format { get; init; } = SampleFormat.Pcm24;
    /// <summary>TPDF dither of ±1 LSB before integer quantisation. Ignored for float output.</summary>
    public bool Dither { get; init; } = true;
    public bool StereoFoldDown { get; init; }
    /// <summary>Adds an EBU Tech 3285 version 2 'bext' chunk carrying the measured loudness.</summary>
    public bool BroadcastWave { get; init; } = true;
    /// <summary>bext description; the session name when empty. Characters outside printable ASCII become '?'.</summary>
    public string? Description { get; init; }
    public DateTime? Created { get; init; }
    public ulong DitherSeed { get; init; } = 0x5EED_C0DE_2026UL;
    public int BitsPerSample => Format switch { SampleFormat.Pcm16 => 16, SampleFormat.Pcm24 => 24, _ => 32 };
}

/// <summary>Writes one WAV file from interleaved samples. The frame count is fixed up front so the header is final.</summary>
internal sealed class WaveWriter : IDisposable
{
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
    private readonly FileStream stream;
    private readonly SampleFormat format;
    private readonly bool dither;
    private readonly long expected, dataBytes, loudnessOffset = -1;
    private readonly byte[] scratch = new byte[65536];
    private long written;
    private ulong noise;

    public WaveWriter(string path, int rate, int channels, uint mask, long frames, ExportOptions options, BroadcastInfo? broadcast)
    {
        format = options.Format;
        dither = options.Dither && format != SampleFormat.Float32;
        noise = options.DitherSeed | 1;
        int bits = options.BitsPerSample, align = channels * bits / 8;
        bool isFloat = format == SampleFormat.Float32, extensible = channels > 2;
        int fmtSize = extensible ? 40 : isFloat ? 18 : 16;
        var bext = options.BroadcastWave ? broadcast?.Encode() : null;
        dataBytes = checked(frames * align);
        long riff = 4 + 8 + fmtSize + (isFloat ? 12 : 0) + (bext is null ? 0 : 8 + bext.Length + (bext.Length & 1)) + 8 + dataBytes + (dataBytes & 1);
        if (riff > uint.MaxValue) throw new InvalidDataException("Output exceeds the RIFF 4 GiB limit. Shorten the session.");
        expected = frames * channels;
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using var header = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        void Tag(string id) => header.Write(Encoding.ASCII.GetBytes(id));
        Tag("RIFF"); header.Write((uint)riff); Tag("WAVE");
        if (bext is not null)
        {
            Tag("bext"); header.Write((uint)bext.Length);
            header.Flush(); loudnessOffset = stream.Position + BroadcastInfo.LoudnessOffset;
            header.Write(bext); if ((bext.Length & 1) != 0) header.Write((byte)0);
        }
        Tag("fmt "); header.Write((uint)fmtSize);
        header.Write((ushort)(extensible ? 0xfffe : isFloat ? 3 : 1)); header.Write((ushort)channels);
        header.Write(rate); header.Write(rate * align); header.Write((ushort)align); header.Write((ushort)bits);
        if (extensible)
        {
            header.Write((ushort)22); header.Write((ushort)bits); header.Write(mask);
            header.Write((isFloat ? Float : Pcm).ToByteArray());
        }
        else if (isFloat) header.Write((ushort)0);
        if (isFloat) { Tag("fact"); header.Write(4u); header.Write((uint)Math.Min(frames, uint.MaxValue)); }
        Tag("data"); header.Write((uint)dataBytes);
    }

    public void Write(ReadOnlySpan<double> samples)
    {
        int size = format switch { SampleFormat.Pcm16 => 2, SampleFormat.Pcm24 => 3, _ => 4 };
        int chunk = scratch.Length / size;
        for (int start = 0; start < samples.Length; start += chunk)
        {
            var part = samples.Slice(start, Math.Min(chunk, samples.Length - start));
            int i = 0;
            foreach (double value in part)
            {
                switch (format)
                {
                    case SampleFormat.Float32:
                        BitConverter.TryWriteBytes(scratch.AsSpan(i), (float)value); i += 4; break;
                    case SampleFormat.Pcm16:
                        BitConverter.TryWriteBytes(scratch.AsSpan(i), (short)Quantize(value, 32768)); i += 2; break;
                    default:
                        int pcm = Quantize(value, 8388608);
                        scratch[i] = (byte)pcm; scratch[i + 1] = (byte)(pcm >> 8); scratch[i + 2] = (byte)(pcm >> 16); i += 3; break;
                }
            }
            stream.Write(scratch, 0, i);
        }
        written += samples.Length;
    }

    /// <summary>Pads the data chunk and stores the measured loudness in the bext chunk.</summary>
    public void Finish(LoudnessReport loudness)
    {
        if (written != expected) throw new InvalidOperationException("Rendered sample count does not match the WAV header.");
        if ((dataBytes & 1) != 0) stream.WriteByte(0);
        if (loudnessOffset >= 0)
        {
            stream.Position = loudnessOffset;
            foreach (double value in new[] { loudness.Integrated, loudness.Range, loudness.TruePeakDb, loudness.MaxMomentary, loudness.MaxShortTerm })
                stream.Write(BitConverter.GetBytes(BroadcastInfo.Hundredths(value)));
        }
        stream.Flush();
    }

    public void Dispose() => stream.Dispose();

    private int Quantize(double value, double scale)
    {
        double x = value * scale;
        if (dither) x += Uniform() + Uniform() - 1;
        return (int)Math.Clamp(Math.Round(x), -scale, scale - 1);
    }
    private double Uniform()
    {
        noise ^= noise << 13; noise ^= noise >> 7; noise ^= noise << 17;
        return (noise >> 11) * (1d / (1UL << 53));
    }
}

/// <summary>EBU Tech 3285 version 2 broadcast extension. Loudness fields are filled in after the render.</summary>
public sealed record BroadcastInfo(string Description, string Originator, string OriginatorReference, DateTime Created, string CodingHistory)
{
    internal const int LoudnessOffset = 256 + 32 + 32 + 10 + 8 + 4 + 4 + 2 + 64;
    internal const short NotSet = 0x7fff;

    internal byte[] Encode()
    {
        var history = Ascii(CodingHistory, 4096);
        var data = new byte[602 + history.Length];
        void Field(string value, int offset, int length) => Ascii(value, length).CopyTo(data, offset);
        Field(Description, 0, 256);
        Field(Originator, 256, 32);
        Field(OriginatorReference, 288, 32);
        Field(Created.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), 320, 10);
        Field(Created.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), 330, 8);
        // Time reference (338, 8 bytes) stays 0: the mix starts at its own zero. Version 2 adds the loudness fields.
        BitConverter.TryWriteBytes(data.AsSpan(346), (ushort)2);
        for (int i = 0; i < 5; i++) BitConverter.TryWriteBytes(data.AsSpan(LoudnessOffset + i * 2), NotSet);
        history.CopyTo(data, 602);
        return data;
    }
    internal static short Hundredths(double value) => double.IsFinite(value) ? (short)Math.Clamp(Math.Round(value * 100), -32767, 32766) : NotSet;
    private static byte[] Ascii(string value, int length)
    {
        var text = new string(value.Select(c => c is >= ' ' and <= '~' or '\r' or '\n' ? c : '?').ToArray());
        return Encoding.ASCII.GetBytes(text.Length > length ? text[..length] : text);
    }
}
