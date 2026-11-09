using System.Text.Json;
using System.Text.Json.Serialization;

namespace XSurLab.Core;

/// <summary>Level and time alignment of one loudspeaker. Positive delay holds a nearer speaker back so all arrive together.</summary>
public sealed record SpeakerTrim(string Speaker, double TrimDb = 0, double DelayMs = 0);

/// <summary>
/// How one output plays one layout in this room. It applies to the monitor only: exports and deliveries never pass through it.
/// </summary>
public sealed record SpeakerCalibration
{
    public const double MinTrimDb = -20, MaxTrimDb = 10, MaxDelayMs = 50;
    /// <summary>Speed of sound at about 20 °C, for converting distances to delays.</summary>
    public const double MetresPerMs = .343;
    public static readonly int[] Crossovers = [60, 80, 100, 120];

    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public SpeakerLayout Layout { get; init; } = SpeakerLayout.Surround51;
    /// <summary>One entry per speaker of the layout, in WAVE channel order.</summary>
    public List<SpeakerTrim> Speakers { get; init; } = [];
    /// <summary>High-passes every main speaker at the crossover and sends the low end, with LFE, to the subwoofer.</summary>
    public bool BassManagement { get; init; }
    public int CrossoverHz { get; init; } = 80;
    /// <summary>The +10 dB in-band gain mixes expect on LFE. Used only with bass management, where the sub is set to match the mains.</summary>
    public bool LfeGain { get; init; } = true;

    public static SpeakerCalibration Flat(SpeakerLayout layout, string deviceId = "", string deviceName = "") => new()
    {
        DeviceId = deviceId, DeviceName = deviceName, Layout = layout,
        Speakers = SpatialMixer.Speakers(layout).Select(s => new SpeakerTrim(s.Name)).ToList()
    };

    public bool IsFlat => !BassManagement && Speakers.All(s => s.TrimDb == 0 && s.DelayMs == 0);
    public static bool CanBassManage(SpeakerLayout layout) => SpatialMixer.Speakers(layout).Any(s => s.IsLfe);

    /// <summary>Reads a speaker's name the way a person says it, e.g. "Ls" → "Left surround".</summary>
    public static string Describe(Speaker speaker) => speaker.Name switch
    {
        "L" => "Front left", "R" => "Front right", "C" => "Centre", "LFE" => "Subwoofer · LFE",
        "Ls" => "Left surround", "Rs" => "Right surround", "Lb" => "Left back", "Rb" => "Right back",
        "Tfl" => "Top front left", "Tfr" => "Top front right", "Tbl" => "Top back left", "Tbr" => "Top back right",
        _ => speaker.Name
    };

    /// <summary>Delays that make every speaker arrive with the farthest one, from listening distances in metres.</summary>
    public static double[] DelaysFromDistances(IReadOnlyList<double> metres)
    {
        if (metres.Count == 0 || metres.Any(m => !double.IsFinite(m) || m < 0 || m > 20))
            throw new ArgumentOutOfRangeException(nameof(metres), "Distances must be between 0 and 20 m.");
        double far = metres.Max();
        return metres.Select(m => Math.Min(MaxDelayMs, Math.Round((far - m) / MetresPerMs, 2))).ToArray();
    }
}

/// <summary>Every calibration on this machine, one per output and layout.</summary>
public sealed record CalibrationFile
{
    public int Version { get; init; } = CalibrationStore.CurrentVersion;
    public List<SpeakerCalibration> Profiles { get; init; } = [];

    public SpeakerCalibration? Find(string deviceId, SpeakerLayout layout) => Profiles.FirstOrDefault(p => p.DeviceId == deviceId && p.Layout == layout);
    /// <summary>A copy with this profile added, or replacing the one for the same output and layout.</summary>
    public CalibrationFile With(SpeakerCalibration profile) => this with
    {
        Profiles = [.. Profiles.Where(p => p.DeviceId != profile.DeviceId || p.Layout != profile.Layout), profile]
    };
}

public static class CalibrationStore
{
    public const int CurrentVersion = 1, MaxProfiles = 256;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<SpeakerLayout>(allowIntegerValues: false) }
    };

    /// <summary>%APPDATA%\XSurLab\calibration.json: it belongs to the room and the machine, not to a session.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XSurLab", "calibration.json");

    public static void Validate(SpeakerCalibration profile)
    {
        if (profile is null) throw new InvalidDataException("Empty calibration profile.");
        if (!Enum.IsDefined(profile.Layout)) throw new InvalidDataException("Unknown speaker layout in calibration.");
        if (string.IsNullOrWhiteSpace(profile.DeviceId) || profile.DeviceId.Length > 512 || profile.DeviceName is null || profile.DeviceName.Length > 256)
            throw new InvalidDataException("A calibration needs the output it was made for.");
        var speakers = SpatialMixer.Speakers(profile.Layout);
        if (profile.Speakers is null || profile.Speakers.Count != speakers.Length || profile.Speakers.Where((s, i) => s is null || s.Speaker != speakers[i].Name).Any())
            throw new InvalidDataException($"A {profile.Layout} calibration lists {string.Join(", ", speakers.Select(s => s.Name))} in that order.");
        foreach (var speaker in profile.Speakers)
        {
            if (!double.IsFinite(speaker.TrimDb) || speaker.TrimDb < SpeakerCalibration.MinTrimDb || speaker.TrimDb > SpeakerCalibration.MaxTrimDb)
                throw new InvalidDataException($"Trim of {speaker.Speaker} must be between {SpeakerCalibration.MinTrimDb} and +{SpeakerCalibration.MaxTrimDb} dB.");
            if (!double.IsFinite(speaker.DelayMs) || speaker.DelayMs < 0 || speaker.DelayMs > SpeakerCalibration.MaxDelayMs)
                throw new InvalidDataException($"Delay of {speaker.Speaker} must be between 0 and {SpeakerCalibration.MaxDelayMs} ms.");
        }
        if (!SpeakerCalibration.Crossovers.Contains(profile.CrossoverHz))
            throw new InvalidDataException($"Crossover must be {string.Join(", ", SpeakerCalibration.Crossovers)} Hz.");
        if (profile.BassManagement && !SpeakerCalibration.CanBassManage(profile.Layout))
            throw new InvalidDataException("Bass management needs a layout with a subwoofer (LFE) channel.");
    }

    public static void Validate(CalibrationFile file)
    {
        if (file is null || file.Version != CurrentVersion) throw new InvalidDataException($"Unsupported calibration version. This build reads version {CurrentVersion}.");
        if (file.Profiles is null || file.Profiles.Count > MaxProfiles) throw new InvalidDataException($"A calibration file holds up to {MaxProfiles} profiles.");
        HashSet<(string, SpeakerLayout)> seen = [];
        foreach (var profile in file.Profiles)
        {
            Validate(profile);
            if (!seen.Add((profile.DeviceId, profile.Layout))) throw new InvalidDataException("Each output and layout has one calibration.");
        }
    }

    /// <summary>An empty file when there is none yet.</summary>
    public static CalibrationFile Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Calibration file is too large.");
        CalibrationFile? file;
        try { file = JsonSerializer.Deserialize<CalibrationFile>(File.ReadAllText(path), Options); }
        catch (JsonException ex) { throw new InvalidDataException("Calibration file is not valid JSON: " + ex.Message, ex); }
        Validate(file ?? throw new InvalidDataException("Empty calibration file."));
        return file;
    }

    /// <summary>
    /// Copies a file that could not be loaded to calibration.json.unreadable-&lt;time&gt; and returns that path, so saving over
    /// it twice (which rotates .bak) cannot lose it. Null when there is no file.
    /// </summary>
    public static string? KeepUnreadable(string path, DateTime now)
    {
        if (!File.Exists(path)) return null;
        // Invariant: a Thai (Buddhist) calendar would otherwise name the copy after the year 2569.
        var stamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var copy = $"{path}.unreadable-{stamp}";
        for (int n = 2; File.Exists(copy); n++) copy = $"{path}.unreadable-{stamp}-{n}";
        File.Copy(path, copy);
        return copy;
    }

    /// <summary>Writes a temporary sibling and swaps it in; the file it replaces is kept as calibration.json.bak.</summary>
    public static void Save(string path, CalibrationFile file)
    {
        Validate(file);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, Options));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
