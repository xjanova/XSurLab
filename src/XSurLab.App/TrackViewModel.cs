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
    /// <summary>Mono and stereo sources are panned; multichannel beds route each channel by its WAVE mask.</summary>
    public bool CanPan => Audio.Channels <= 2;
    public string Description => (CanPan ? $"{Audio.Channels} ch" : $"{Audio.Channels} ch bed · {string.Join(" ", SpatialMixer.Bits(Audio.ChannelMask).Select(SpatialMixer.ChannelName))}")
        + $" · {Audio.SampleRate / 1000d:0.#} kHz · {Audio.Duration:0.00} s{(Audio.IsStreamed ? " · streamed from disk" : "")}";
    public double GainDb { get => settings.GainDb; set { settings = settings with { GainDb = value }; Changed(); } }
    public double Azimuth { get => settings.Azimuth; set { settings = settings with { Azimuth = value }; Changed(); } }
    public double Elevation { get => settings.Elevation; set { settings = settings with { Elevation = value }; Changed(); } }
    public double Spread { get => settings.Spread; set { settings = settings with { Spread = value }; Changed(); } }
    public TrackEq Eq { get => settings.Eq; set { settings = settings with { Eq = value }; Changed(); } }
    public double LfeDb { get => settings.LfeDb; set { settings = settings with { LfeDb = value }; Changed(); } }
    public double StartSeconds { get => settings.StartSeconds; set { settings = settings with { StartSeconds = value }; Changed(); } }
    public bool Mute { get => settings.Mute; set { settings = settings with { Mute = value }; Changed(); } }
    public bool Solo { get => settings.Solo; set { settings = settings with { Solo = value }; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
