namespace XSurLab.Core;

public sealed record Speaker(string Name, double Azimuth, bool IsLfe = false, double Elevation = 0);

public static class SpatialMixer
{
    public static Speaker[] Speakers(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Stereo => [new("L", -30), new("R", 30)],
        SpeakerLayout.Surround51 => [new("L", -30), new("R", 30), new("C", 0), new("LFE", 0, true), new("Ls", -110), new("Rs", 110)],
        SpeakerLayout.Surround51Back => [new("L", -30), new("R", 30), new("C", 0), new("LFE", 0, true), new("Lb", -110), new("Rb", 110)],
        SpeakerLayout.Surround71 => [new("L", -30), new("R", 30), new("C", 0), new("LFE", 0, true), new("Lb", -150), new("Rb", 150), new("Ls", -90), new("Rs", 90)],
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    public static uint ChannelMask(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Stereo => 0x3,
        SpeakerLayout.Surround51 => 0x60f,
        SpeakerLayout.Surround51Back => 0x3f,
        SpeakerLayout.Surround71 => 0x63f,
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

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
        output[0] += source[2] * Math.Sqrt(.5);
        output[1] += source[2] * Math.Sqrt(.5);
        for (int i = 4; i < source.Length; i++) output[i % 2] += source[i] * Math.Sqrt(.5);
    }
}
