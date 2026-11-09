using System.ComponentModel;
using System.Runtime.CompilerServices;
using XSurLab.Core;

namespace XSurLab.App;

public sealed class TrackViewModel(TrackSettings settings, AudioBuffer audio) : INotifyPropertyChanged
{
    private TrackSettings settings = settings;
    public AudioBuffer Audio { get; } = audio;
    public TrackSettings Snapshot => settings;
    public string Name { get => settings.Name; set { settings = settings with { Name = value }; Changed(); } }
    public string MediaPath => settings.MediaPath;
    public string Description => $"{Audio.Channels} ch · {Audio.SampleRate / 1000d:0.#} kHz · {Audio.Duration:0.00} s";
    public double GainDb { get => settings.GainDb; set { settings = settings with { GainDb = value }; Changed(); } }
    public double Azimuth { get => settings.Azimuth; set { settings = settings with { Azimuth = value }; Changed(); } }
    public double LfeDb { get => settings.LfeDb; set { settings = settings with { LfeDb = value }; Changed(); } }
    public double StartSeconds { get => settings.StartSeconds; set { settings = settings with { StartSeconds = value }; Changed(); } }
    public bool Mute { get => settings.Mute; set { settings = settings with { Mute = value }; Changed(); } }
    public bool Solo { get => settings.Solo; set { settings = settings with { Solo = value }; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
