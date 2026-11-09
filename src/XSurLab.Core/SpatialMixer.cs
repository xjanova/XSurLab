namespace XSurLab.Core;

/// <summary>A loudspeaker position. Bit is its WAVE channel-mask bit (0 when the speaker has no file identity).</summary>
public sealed record Speaker(string Name, double Azimuth, bool IsLfe = false, double Elevation = 0, uint Bit = 0);

public static class SpatialMixer
{
    public const uint FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4, LowFrequency = 0x8, BackLeft = 0x10, BackRight = 0x20,
        FrontLeftOfCenter = 0x40, FrontRightOfCenter = 0x80, BackCenter = 0x100, SideLeft = 0x200, SideRight = 0x400,
        TopCenter = 0x800, TopFrontLeft = 0x1000, TopFrontCenter = 0x2000, TopFrontRight = 0x4000,
        TopBackLeft = 0x8000, TopBackCenter = 0x10000, TopBackRight = 0x20000;
    private const uint KnownBits = 0x3ffff;
    private static readonly double Half = Math.Sqrt(.5);

    private static readonly Speaker L = new("L", -30, Bit: FrontLeft), R = new("R", 30, Bit: FrontRight), C = new("C", 0, Bit: FrontCenter),
        Lfe = new("LFE", 0, true, Bit: LowFrequency);

    public static Speaker[] Speakers(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Stereo => [L, R],
        SpeakerLayout.Surround51 => [L, R, C, Lfe, new("Ls", -110, Bit: SideLeft), new("Rs", 110, Bit: SideRight)],
        SpeakerLayout.Surround51Back => [L, R, C, Lfe, new("Lb", -110, Bit: BackLeft), new("Rb", 110, Bit: BackRight)],
        SpeakerLayout.Surround71 => [L, R, C, Lfe, new("Lb", -150, Bit: BackLeft), new("Rb", 150, Bit: BackRight),
            new("Ls", -90, Bit: SideLeft), new("Rs", 90, Bit: SideRight)],
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    public static uint ChannelMask(SpeakerLayout layout) => Speakers(layout).Aggregate(0u, (mask, s) => mask | s.Bit);

    /// <summary>Set bits of a channel mask in file order (ascending).</summary>
    public static uint[] Bits(uint mask) => Enumerable.Range(0, 32).Select(i => 1u << i).Where(bit => (mask & bit) != 0).ToArray();

    /// <summary>Mask implied by a plain (non-extensible) WAV: mono centre, stereo L/R, and the first N bits up to 6 channels.</summary>
    public static uint DefaultMask(int channels) => channels switch
    {
        1 => FrontCenter, 2 => FrontLeft | FrontRight, >= 3 and <= 6 => (1u << channels) - 1, _ => 0
    };

    public static bool IsValidMask(uint mask, int channels) => (mask & ~KnownBits) == 0 && System.Numerics.BitOperations.PopCount(mask) == channels;

    public static string ChannelName(uint bit) => bit switch
    {
        FrontLeft => "L", FrontRight => "R", FrontCenter => "C", LowFrequency => "LFE", BackLeft => "Lb", BackRight => "Rb",
        FrontLeftOfCenter => "Lc", FrontRightOfCenter => "Rc", BackCenter => "Cb", SideLeft => "Ls", SideRight => "Rs",
        TopCenter => "Tc", TopFrontLeft => "Tfl", TopFrontCenter => "Tfc", TopFrontRight => "Tfr",
        TopBackLeft => "Tbl", TopBackCenter => "Tbc", TopBackRight => "Tbr", _ => "?"
    };

    /// <summary>
    /// Gains from one channel of a multichannel source to each speaker of a layout. A channel the layout has goes straight
    /// through. Otherwise: side and back surrounds substitute for each other, then fold to the front pair at −3 dB;
    /// centre folds to L/R at −3 dB; heights fold to the speaker below them at −3 dB; LFE is dropped without an LFE speaker.
    /// </summary>
    public static double[] Route(SpeakerLayout layout, uint bit)
    {
        var speakers = Speakers(layout);
        var gains = new double[speakers.Length];
        bool Has(uint b) => speakers.Any(s => s.Bit == b);
        void Add(uint b, double gain)
        {
            int index = Array.FindIndex(speakers, s => s.Bit == b);
            if (index >= 0) { gains[index] += gain; return; }
            switch (b)
            {
                case FrontCenter: Add(FrontLeft, gain * Half); Add(FrontRight, gain * Half); break;
                case LowFrequency: break;
                case SideLeft: if (Has(BackLeft)) Add(BackLeft, gain); else Add(FrontLeft, gain * Half); break;
                case SideRight: if (Has(BackRight)) Add(BackRight, gain); else Add(FrontRight, gain * Half); break;
                case BackLeft: if (Has(SideLeft)) Add(SideLeft, gain); else Add(FrontLeft, gain * Half); break;
                case BackRight: if (Has(SideRight)) Add(SideRight, gain); else Add(FrontRight, gain * Half); break;
                case BackCenter: Add(BackLeft, gain * Half); Add(BackRight, gain * Half); break;
                case FrontLeftOfCenter or FrontRightOfCenter:
                    var pan = Pan(layout, b == FrontLeftOfCenter ? -15 : 15);
                    for (int i = 0; i < gains.Length; i++) gains[i] += gain * pan[i];
                    break;
                case TopFrontLeft: Add(FrontLeft, gain * Half); break;
                case TopFrontRight: Add(FrontRight, gain * Half); break;
                case TopFrontCenter: Add(FrontCenter, gain * Half); break;
                case TopBackLeft: Add(BackLeft, gain * Half); break;
                case TopBackRight: Add(BackRight, gain * Half); break;
                case TopBackCenter: Add(BackCenter, gain * Half); break;
                case TopCenter: foreach (var top in new[] { TopFrontLeft, TopFrontRight, TopBackLeft, TopBackRight }) Add(top, gain * .5); break;
                default: throw new ArgumentOutOfRangeException(nameof(bit), $"Unknown channel bit 0x{bit:X}.");
            }
        }
        Add(bit, 1);
        return gains;
    }

    public static double[] Pan(SpeakerLayout layout, double azimuth)
    {
        var speakers = Speakers(layout);
        var gains = new double[speakers.Length];
        if (layout == SpeakerLayout.Stereo)
        {
            var position = Math.Clamp(azimuth / 30, -1, 1);
            var angle = (position + 1) * Math.PI / 4;
            gains[0] = Math.Cos(angle);
            gains[1] = Math.Sin(angle);
            return gains;
        }
        double Normalize(double degrees) => (degrees % 360 + 360) % 360;
        var ring = speakers.Select((s, i) => (Angle: Normalize(s.Azimuth), Index: i, s.IsLfe))
            .Where(s => !s.IsLfe).OrderBy(s => s.Angle).ToArray();
        var target = Normalize(azimuth);
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            var end = b.Angle + (i == ring.Length - 1 ? 360 : 0);
            var value = target < a.Angle ? target + 360 : target;
            if (value < a.Angle || value > end) continue;
            var phase = (value - a.Angle) / (end - a.Angle) * Math.PI / 2;
            gains[a.Index] = Math.Cos(phase);
            gains[b.Index] = Math.Sin(phase);
            break;
        }
        return gains;
    }

    public static void FoldStereo(ReadOnlySpan<double> source, SpeakerLayout layout, Span<double> output)
    {
        output[0] = source[0];
        output[1] = source[1];
        if (layout == SpeakerLayout.Stereo) return;
        output[0] += source[2] * Half;
        output[1] += source[2] * Half;
        for (int i = 4; i < source.Length; i++) output[i % 2] += source[i] * Half;
    }
}
