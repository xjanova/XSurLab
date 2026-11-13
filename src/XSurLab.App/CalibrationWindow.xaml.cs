using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using XSurLab.Audio;
using XSurLab.Core;

namespace XSurLab.App;

public sealed record CrossoverOption(int Hz)
{
    public override string ToString() => $"{Hz} Hz";
}

/// <summary>
/// Plays test noise on one speaker at a time through the chosen output and mode, while trims and delays are set live.
/// The window owns its stream; Stopped completes once the output is released, so the main window can play again.
/// </summary>
public partial class CalibrationWindow : Window, INotifyPropertyChanged
{
    private const double CycleSeconds = 3;
    private readonly AudioDevice output;
    private readonly OutputMode mode;
    private readonly SpeakerLayout layout;
    private readonly int sampleRate;
    private readonly int[] order;
    private readonly DispatcherTimer cycle = new() { Interval = TimeSpan.FromSeconds(CycleSeconds) };
    private TestSignal? signal;
    private MonitorChain? chain;
    private WasapiPlayback? player;
    private bool bass, lfeGain, opening, closed;
    // The speaker asked for last; the noise moves there once the output is open.
    private int crossover, wanted = -1;
    private double monitorVolume;
    private string status = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public SpeakerRow[] Speakers { get; }
    public CrossoverOption[] Crossovers { get; } = SpeakerCalibration.Crossovers.Select(hz => new CrossoverOption(hz)).ToArray();
    public string Header { get; }
    public bool CanBassManage { get; }
    public bool CanEdit => !opening;
    public bool BassManagement { get => bass; set { bass = value && CanBassManage; Changed(); Changed(nameof(CanSetCrossover)); Changed(nameof(BassHint)); Publish(); } }
    public bool CanSetCrossover => bass;
    public int CrossoverHz { get => crossover; set { crossover = value; Changed(); Changed(nameof(BassHint)); Publish(); } }
    public bool LfeGain { get => lfeGain; set { lfeGain = value; Changed(); Changed(nameof(BassHint)); Publish(); } }
    public string BassHint => !CanBassManage ? "Bass management needs a layout with a subwoofer; stereo fold-down has none."
        : bass ? $"Small speakers roll off below {crossover} Hz at 24 dB/oct into the subwoofer; Large speakers keep their full range. "
            + (lfeGain ? "LFE +10 dB is added here, so trim the subwoofer to read the same as the mains." : "LFE is not boosted, so set the subwoofer to read 10 dB above the mains.")
            + " Test noise skips bass management so each speaker is measured on its own."
        : "Bass management off: every channel goes to its own speaker and LFE is not boosted, so set the subwoofer (its trim or its own level) to read 10 dB above the mains.";
    public double MonitorVolume
    {
        get => monitorVolume;
        set { monitorVolume = Math.Clamp(value, 0, 1); if (player is not null) player.Volume = monitorVolume; Changed(); }
    }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string CycleLabel => cycle.IsEnabled ? "■ Stop cycle" : "▶ Cycle all speakers";
    /// <summary>The calibration to keep after Save.</summary>
    public SpeakerCalibration? Result { get; private set; }
    /// <summary>Completes when this window's stream has released the output.</summary>
    public Task Stopped { get; private set; } = Task.CompletedTask;

    /// <param name="released">Completes when the main window's stream has released the output.</param>
    public CalibrationWindow(AudioDevice output, OutputMode mode, SpeakerLayout layout, int sampleRate, double monitorVolume, SpeakerCalibration calibration,
        string layoutName, Task released)
    {
        this.output = output; this.mode = mode; this.layout = layout; this.sampleRate = sampleRate; this.monitorVolume = monitorVolume;
        Stopped = released;
        var speakers = SpatialMixer.Speakers(layout);
        Speakers = speakers.Select((s, i) => new SpeakerRow(i, s, calibration.Speakers[i], Publish, SpeakerCalibration.CanBassManage(layout))).ToArray();
        order = TestSignal.Order(layout);
        CanBassManage = SpeakerCalibration.CanBassManage(layout);
        bass = calibration.BassManagement && CanBassManage; crossover = calibration.CrossoverHz; lfeGain = calibration.LfeGain;
        Header = $"{output.Name} · {layoutName} · {mode} · {sampleRate / 1000d:0.#} kHz";
        cycle.Tick += (_, _) => Advance();
        InitializeComponent();
        DataContext = this;
        Status = mode == OutputMode.Exclusive
            ? "Exclusive: Windows volume does not apply. Set MONITOR before the first noise."
            : "Shared: Windows volume and sound effects apply. Turn off room correction or virtual surround in Windows while you calibrate.";
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private SpeakerCalibration Build() => new()
    {
        DeviceId = output.Id, DeviceName = output.Name.Length > 256 ? output.Name[..256] : output.Name, Layout = layout,
        Speakers = Speakers.Select(s => new SpeakerTrim(s.Name, s.TrimDb, s.DelayMs, s.Small)).ToList(),
        BassManagement = bass, CrossoverHz = crossover, LfeGain = lfeGain
    };
    private void Publish() { if (chain is not null) chain.Update(Build()); }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SpeakerRow row }) return;
        StopCycle();
        // A second click on the speaker that is playing silences it.
        Play(row.IsPlaying ? -1 : row.Index);
    }
    private void Cycle_Click(object sender, RoutedEventArgs e)
    {
        if (cycle.IsEnabled) { StopCycle(); Play(-1); return; }
        Play(order[0]);
        cycle.Start(); Changed(nameof(CycleLabel));
    }
    private void Advance() => Play(order[(Array.IndexOf(order, wanted) + 1) % order.Length]);
    private void StopCycle() { cycle.Stop(); Changed(nameof(CycleLabel)); }
    private void StopNoise_Click(object sender, RoutedEventArgs e) { StopCycle(); Play(-1); }

    /// <summary>Moves the noise to a speaker (−1 = silence), opening the output on first use.</summary>
    private void Play(int index)
    {
        wanted = index;
        foreach (var row in Speakers) row.IsPlaying = row.Index == index;
        if (signal is not null) { signal.SpeakerIndex = index; return; }
        if (index >= 0) _ = OpenAsync();
    }

    private async Task OpenAsync()
    {
        if (opening || closed) return;
        opening = true; Changed(nameof(CanEdit));
        WasapiPlayback? media = null;
        try
        {
            // The main window's stream, or an earlier one of ours, may still be releasing the output.
            await Stopped;
            // Measuring the noise level takes a moment, so it is generated off the UI thread.
            var noise = await Task.Run(() => new TestSignal(sampleRate, layout));
            if (closed || wanted < 0) return;
            noise.SpeakerIndex = wanted;
            var calibrated = new MonitorChain(noise, layout, Build(), bypassBassManagement: true);
            var started = new Progress<PlaybackInfo>(info =>
            {
                if (player == media && !closed) Status = $"Noise on · {info.Mode} {info.Format} · {info.Channels} ch / {info.SampleRate / 1000d:0.#} kHz";
            });
            media = new WasapiPlayback(calibrated, output.Id, MonitorVolume, started, mode);
            signal = noise; chain = calibrated; player = media;
            Stopped = media.Completion.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
            Status = "Opening the output…";
        }
        catch (Exception ex) { Fail(ex.Message); }
        finally { opening = false; if (!closed) Changed(nameof(CanEdit)); }
        if (media is null) return;
        try { await media.Completion; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (player == media && !closed) Fail(ex.Message); return; }
    }

    private void Fail(string message)
    {
        StopCycle();
        Release();
        foreach (var row in Speakers) row.IsPlaying = false;
        Status = message;
    }

    // The noise fades out over 20 ms first; the stream closes once that has reached the speaker.
    private void Release()
    {
        var media = player;
        if (signal is not null) signal.SpeakerIndex = -1;
        player = null; signal = null; chain = null;
        if (media is null) return;
        var completion = media.Completion;
        Stopped = Task.Delay(150).ContinueWith(_ => media.Dispose(), TaskScheduler.Default)
            .ContinueWith(_ => completion, TaskScheduler.Default).Unwrap()
            .ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
    }

    private void Distances_Click(object sender, RoutedEventArgs e)
    {
        var metres = new List<double>();
        foreach (var row in Speakers)
        {
            if (!row.TryDistance(out var value)) { Status = $"Enter the distance of {row.Name} in metres (0–20) first."; return; }
            metres.Add(value);
        }
        var delays = SpeakerCalibration.DelaysFromDistances(metres);
        for (int i = 0; i < Speakers.Length; i++) Speakers[i].DelayMs = delays[i];
        Status = delays.Any(d => d >= SpeakerCalibration.MaxDelayMs)
            ? $"Delays set · one or more reached the {SpeakerCalibration.MaxDelayMs} ms limit."
            : $"Delays set from distances · the farthest speaker ({Speakers[Array.IndexOf(delays, 0d)].Name}) has none.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Set every trim and delay to 0, set mains to Small and turn bass management off?\nCancel in this window still keeps the saved calibration.",
                "Reset calibration", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var row in Speakers) row.Reset();
        BassManagement = false;
        Status = "Flat · Save keeps it, Cancel keeps the previous calibration.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Build();
        DialogResult = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        cycle.Stop();
        Release();
    }
}

/// <summary>One speaker's trim, delay and optional listening distance in the calibration window.</summary>
public sealed class SpeakerRow : INotifyPropertyChanged
{
    private readonly Action changed;
    private double trim, delay;
    private string distance = "";
    private bool playing, small;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SpeakerRow(int index, Speaker speaker, SpeakerTrim value, Action changed, bool canBassManage)
    {
        Index = index; Name = speaker.Name; Description = SpeakerCalibration.Describe(speaker);
        trim = value.TrimDb; delay = value.DelayMs; small = value.Small; this.changed = changed;
        IsLfe = speaker.IsLfe; CanSetSize = canBassManage && !IsLfe;
    }

    public int Index { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsLfe { get; }
    public bool CanSetSize { get; }
    public bool Small { get => small; set { if (small == value) return; small = value; Changed(); Changed(nameof(SizeLabel)); changed(); } }
    public string SizeLabel => small ? "Small" : "Large";
    public string SizeName => $"Redirect bass of {Description} to the subwoofer";
    public string TestName => $"Test noise on {Description}";
    public string TrimName => $"Trim of {Description}";
    public string DelayName => $"Delay of {Description}";
    public string DistanceName => $"Distance of {Description} in metres";
    public double TrimDb { get => trim; set { trim = Math.Clamp(Math.Round(value * 2) / 2, SpeakerCalibration.MinTrimDb, SpeakerCalibration.MaxTrimDb); Changed(); Changed(nameof(TrimText)); changed(); } }
    public double DelayMs { get => delay; set { delay = Math.Clamp(Math.Round(value, 2), 0, SpeakerCalibration.MaxDelayMs); Changed(); Changed(nameof(DelayText)); changed(); } }
    public string TrimText => trim.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture);
    public string DelayText => delay.ToString("0.00", CultureInfo.InvariantCulture);
    public string Distance { get => distance; set { distance = value ?? ""; Changed(); } }
    public bool IsPlaying { get => playing; set { if (playing == value) return; playing = value; Changed(); Changed(nameof(TestLabel)); } }
    public string TestLabel => playing ? "■ On" : "▶ Test";

    public bool TryDistance(out double metres)
    {
        var text = distance.Trim();
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out metres)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out metres);
        return parsed && double.IsFinite(metres) && metres is >= 0 and <= 20;
    }

    public void Reset() { TrimDb = 0; DelayMs = 0; Small = true; }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
