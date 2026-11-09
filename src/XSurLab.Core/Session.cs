using System.Text.Json;
using System.Text.Json.Serialization;

namespace XSurLab.Core;

public enum SpeakerLayout { Stereo, Surround51, Surround71, Surround51Back, Surround514, Surround714 }

public sealed record TrackSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Audio";
    public string MediaPath { get; init; } = "";
    public double GainDb { get; init; }
    public double Azimuth { get; init; }
    /// <summary>Degrees above ear level, 0–90. Heard only on layouts with height speakers.</summary>
    public double Elevation { get; init; }
    /// <summary>Width in degrees of the arc the source is spread over, 0–360.</summary>
    public double Spread { get; init; }
public TrackEq Eq { get; init; } = TrackEq.Flat;
    public double LfeDb { get; init; } = -60;
    public double StartSeconds { get; init; }
    public bool Mute { get; init; }
    public bool Solo { get; init; }
}

public sealed record SessionDocument
{
    public int Version { get; init; } = SessionStore.CurrentVersion;
    public string Name { get; init; } = "Untitled session";
    public int SampleRate { get; init; } = 48000;
    public SpeakerLayout Layout { get; init; } = SpeakerLayout.Surround51;
    public double MasterGainDb { get; init; } = -6;
    public List<TrackSettings> Tracks { get; init; } = [];
}

public static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<SpeakerLayout>(allowIntegerValues: false) }
    };

    /// <summary>2 adds elevation, spread, the track EQ and the 5.1.4 / 7.1.4 layouts. Version 1 files load flat and at ear level.</summary>
    public const int CurrentVersion = 2;

    public static void Validate(SessionDocument session)
    {
        if (session.Version is < 1 or > CurrentVersion)
            throw new InvalidDataException($"Unsupported session version {session.Version}. This build reads versions 1–{CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(session.Name) || session.Name.Length > 200)
            throw new InvalidDataException("Session name must contain 1–200 characters.");
        if (session.SampleRate is not (44100 or 48000 or 96000))
            throw new InvalidDataException("Choose 44.1, 48 or 96 kHz.");
        if (!Enum.IsDefined(session.Layout)) throw new InvalidDataException("Unknown speaker layout.");
        Range(session.MasterGainDb, -60, 6, "Master gain");
        if (session.Tracks is null || session.Tracks.Count > 32)
            throw new InvalidDataException("A session supports up to 32 tracks.");
        HashSet<Guid> ids = [];
        foreach (var track in session.Tracks)
        {
            if (track is null || track.Id == Guid.Empty || !ids.Add(track.Id))
                throw new InvalidDataException("Track IDs must be unique.");
            if (string.IsNullOrWhiteSpace(track.Name) || track.Name.Length > 200 || string.IsNullOrWhiteSpace(track.MediaPath))
                throw new InvalidDataException("Each track needs a name and a media path.");
            Range(track.GainDb, -60, 12, "Track gain");
            Range(track.Azimuth, -180, 180, "Azimuth");
            Range(track.Elevation, 0, 90, "Elevation");
            Range(track.Spread, 0, 360, "Spread");
            if (track.Eq is null) throw new InvalidDataException("Each track needs an EQ (use the flat EQ).");
            for (int b = 0; b < TrackEq.Count; b++)
            {
                var band = track.Eq.Band(b) ?? throw new InvalidDataException($"EQ {TrackEq.Names[b]} is missing.");
                Range(band.Frequency, 20, 20000, $"EQ {TrackEq.Names[b]} frequency");
                Range(band.GainDb, -24, 24, $"EQ {TrackEq.Names[b]} gain");
                Range(band.Q, .1, 10, $"EQ {TrackEq.Names[b]} Q");
            }
            Range(track.LfeDb, -60, 0, "LFE send");
            Range(track.StartSeconds, 0, 3600, "Start offset");
        }
    }

    private static void Range(double value, double min, double max, string label)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException($"{label} must be between {min} and {max}.");
    }

    public static SessionDocument Load(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Session file is too large.");
        var session = JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Empty session file.");
        Validate(session);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return session with { Version = CurrentVersion, Tracks = session.Tracks.Select(t => t with
        {
            MediaPath = Path.GetFullPath(t.MediaPath, folder)
        }).ToList() };
    }

    public static void Save(string path, SessionDocument session)
    {
        Validate(session);
        path = Path.GetFullPath(path);
        if (session.Tracks.Any(t => string.Equals(Path.GetFullPath(t.MediaPath), path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A session file cannot overwrite source audio.");
        var folder = Path.GetDirectoryName(path)!;
        var portable = session with { Version = CurrentVersion, Tracks = session.Tracks.Select(t => t with
        {
            MediaPath = Path.GetRelativePath(folder, Path.GetFullPath(t.MediaPath))
        }).ToList() };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(portable, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

