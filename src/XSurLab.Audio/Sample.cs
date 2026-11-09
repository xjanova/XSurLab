using System.Buffers.Binary;

namespace XSurLab.Audio;

/// <summary>Packs monitor samples (full scale ±1) into the endpoint's buffer layout, little-endian.</summary>
internal static class Sample
{
    internal static int Bytes(EndpointEncoding encoding) => encoding switch
    {
        EndpointEncoding.Int24 => 3, EndpointEncoding.Int16 => 2, _ => 4
    };
    internal static string Name(EndpointEncoding encoding) => encoding switch
    {
        EndpointEncoding.Float32 => "float32", EndpointEncoding.Int24In32 => "PCM 24-in-32", EndpointEncoding.Int32 => "PCM 32",
        EndpointEncoding.Int24 => "PCM 24", _ => "PCM 16"
    };

    // Integer formats round to the nearest step and hold +1.0 at the largest code; the monitor rejects anything beyond ±1 before this.
    // There is no dither: this is a listening path, and export keeps its own TPDF dither.
    internal static void Encode(ReadOnlySpan<double> source, Span<byte> destination, EndpointEncoding encoding)
    {
        int size = Bytes(encoding);
        if (destination.Length < source.Length * size) throw new ArgumentException("Destination is too small.", nameof(destination));
        for (int i = 0; i < source.Length; i++)
        {
            var target = destination.Slice(i * size, size);
            double value = source[i];
            switch (encoding)
            {
                case EndpointEncoding.Float32: BinaryPrimitives.WriteSingleLittleEndian(target, (float)value); break;
                case EndpointEncoding.Int16: BinaryPrimitives.WriteInt16LittleEndian(target, (short)Quantize(value, 15)); break;
                case EndpointEncoding.Int24In32: BinaryPrimitives.WriteInt32LittleEndian(target, (int)Quantize(value, 23) << 8); break;
                case EndpointEncoding.Int32: BinaryPrimitives.WriteInt32LittleEndian(target, (int)Quantize(value, 31)); break;
                default:
                    int code = (int)Quantize(value, 23);
                    target[0] = (byte)code; target[1] = (byte)(code >> 8); target[2] = (byte)(code >> 16);
                    break;
            }
        }
    }
    private static long Quantize(double value, int bits)
    {
        double scale = 1L << bits;
        return (long)Math.Clamp(Math.Round(value * scale), -scale, scale - 1);
    }

    /// <summary>Exclusive mode wants a period that is a whole number of frames; WASAPI reports the frame count it can align to.</summary>
    internal static long AlignedPeriod(uint frames, int rate) => (long)Math.Round(10_000_000d * frames / rate);
}
