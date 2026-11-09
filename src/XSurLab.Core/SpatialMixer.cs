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
    // Height speakers sit 45° up, at ±45° and ±135°: the usual 5.1.4 / 7.1.4 home placement.
    private static readonly Speaker[] Heights = [new("Tfl", -45, Elevation: 45, Bit: TopFrontLeft), new("Tfr", 45, Elevation: 45, Bit: TopFrontRight),
        new("Tbl", -135, Elevation: 45, Bit: TopBackLeft), new("Tbr", 135, Elevation: 45, Bit: TopBackRight)];
    private const double HeightElevation = 45;

    /// <summary>Speakers in WAVE channel order (ascending mask bit), which is also the order of rendered channels.</summary>
    public static Speaker[] Speakers(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Stereo => [L, R],
        SpeakerLayout.Surround51 => [L, R, C, Lfe, new("Ls", -110, Bit: SideLeft), new("Rs", 110, Bit: SideRight)],
        SpeakerLayout.Surround51Back => [L, R, C, Lfe, new("Lb", -110, Bit: BackLeft), new("Rb", 110, Bit: BackRight)],
        SpeakerLayout.Surround71 => [L, R, C, Lfe, new("Lb", -150, Bit: BackLeft), new("Rb", 150, Bit: BackRight),
            new("Ls", -90, Bit: SideLeft), new("Rs", 90, Bit: SideRight)],
        SpeakerLayout.Surround514 => [L, R, C, Lfe, new("Ls", -110, Bit: SideLeft), new("Rs", 110, Bit: SideRight), .. Heights],
        SpeakerLayout.Surround714 => [L, R, C, Lfe, new("Lb", -150, Bit: BackLeft), new("Rb", 150, Bit: BackRight),
            new("Ls", -90, Bit: SideLeft), new("Rs", 90, Bit: SideRight), .. Heights],
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    public static bool HasHeights(SpeakerLayout layout) => layout is SpeakerLayout.Surround514 or SpeakerLayout.Surround714;

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
                case TopFrontCenter:
                    if (Has(TopFrontLeft)) { Add(TopFrontLeft, gain * Half); Add(TopFrontRight, gain * Half); } else Add(FrontCenter, gain * Half);
                    break;
                case TopBackLeft: Add(BackLeft, gain * Half); break;
                case TopBackRight: Add(BackRight, gain * Half); break;
                case TopBackCenter:
                    if (Has(TopBackLeft)) { Add(TopBackLeft, gain * Half); Add(TopBackRight, gain * Half); } else Add(BackCenter, gain * Half);
                    break;
                case TopCenter: foreach (var top in new[] { TopFrontLeft, TopFrontRight, TopBackLeft, TopBackRight }) Add(top, gain * .5); break;
                default: throw new ArgumentOutOfRangeException(nameof(bit), $"Unknown channel bit 0x{bit:X}.");
            }
        }
        Add(bit, 1);
        return gains;
    }

    /// <summary>
    /// Power-preserving gains for a point source. Azimuth pans pairwise (sine/cosine) around the ear-level ring. On layouts
    /// with height speakers, elevation crossfades power to the height ring by 45° and spreads it evenly over all heights by 90°;
    /// elsewhere elevation is ignored and the source stays at ear level. Spread is the width in degrees of an arc of equally
    /// weighted virtual sources centred on the azimuth; their powers are averaged, so the total power stays 1.
    /// </summary>
    public static double[] Pan(SpeakerLayout layout, double azimuth, double elevation = 0, double spread = 0)
    {
        var speakers = Speakers(layout);
        var power = new double[speakers.Length];
        double lift = HasHeights(layout) ? Math.Clamp(elevation, 0, 90) : 0;
        double upper = lift >= HeightElevation ? 1 : Math.Pow(Math.Sin(lift / HeightElevation * Math.PI / 2), 2);
        double overhead = Math.Max(0, (lift - HeightElevation) / (90 - HeightElevation));
        double width = Math.Clamp(spread, 0, 360);
        int points = width == 0 ? 1 : (int)Math.Ceiling(width / 5) + 1;
        for (int i = 0; i < points; i++)
        {
            double angle = points == 1 ? azimuth : azimuth - width / 2 + (i + .5) * width / points;
            if (upper < 1) Ring(layout, speakers, heights: false, angle, power, (1 - upper) / points);
            if (upper > 0 && overhead < 1) Ring(layout, speakers, heights: true, angle, power, upper * (1 - overhead) / points);
        }
        if (overhead > 0)
        {
            int tops = speakers.Count(s => s.Elevation > 0);
            for (int i = 0; i < speakers.Length; i++) if (speakers[i].Elevation > 0) power[i] += overhead / tops;
        }
        return power.Select(Math.Sqrt).ToArray();
    }

    // Adds weight × gain² of a pairwise pan on one ring (ear level or heights) to power.
    private static void Ring(SpeakerLayout layout, Speaker[] speakers, bool heights, double azimuth, double[] power, double weight)
    {
        if (layout == SpeakerLayout.Stereo)
        {
            var angle = (Math.Clamp(Math.IEEERemainder(azimuth, 360) / 30, -1, 1) + 1) * Math.PI / 4;
            power[0] += weight * Math.Cos(angle) * Math.Cos(angle);
            power[1] += weight * Math.Sin(angle) * Math.Sin(angle);
            return;
        }
        double Normalize(double degrees) => (degrees % 360 + 360) % 360;
        var ring = speakers.Select((s, i) => (Angle: Normalize(s.Azimuth), Index: i, Speaker: s))
            .Where(s => !s.Speaker.IsLfe && s.Speaker.Elevation > 0 == heights).OrderBy(s => s.Angle).ToArray();
        var target = Normalize(azimuth);
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            var end = b.Angle + (i == ring.Length - 1 ? 360 : 0);
            var value = target < a.Angle ? target + 360 : target;
            if (value < a.Angle || value > end) continue;
            var phase = (value - a.Angle) / (end - a.Angle) * Math.PI / 2;
            power[a.Index] += weight * Math.Cos(phase) * Math.Cos(phase);
            power[b.Index] += weight * Math.Sin(phase) * Math.Sin(phase);
            return;
        }
    }

    public static void FoldStereo(ReadOnlySpan<double> source, SpeakerLayout layout, Span<double> output)
    {
        output[0] = source[0];
        output[1] = source[1];
        if (layout == SpeakerLayout.Stereo) return;
        output[0] += source[2] * Half;
        output[1] += source[2] * Half;
        // Every layout lists surrounds and heights as left/right pairs from index 4, so parity picks the side.
        for (int i = 4; i < source.Length; i++) output[i % 2] += source[i] * Half;
    }
}
