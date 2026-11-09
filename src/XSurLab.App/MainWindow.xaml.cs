using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using XSurLab.Audio;
using XSurLab.Core;

namespace XSurLab.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public LayoutOption[] Layouts { get; } = [new(SpeakerLayout.Stereo, "Stereo 2.0"), new(SpeakerLayout.Surround51, "5.1 Side"), new(SpeakerLayout.Surround51Back, "5.1 Back"), new(SpeakerLayout.Surround71, "7.1")];
    public int[] Rates { get; } = [44100, 48000, 96000];
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];
    private string sessionName = "Untitled session", status = "Ready · import WAV to begin.", renderSummary = "Live WASAPI monitor · export remains independent of monitor level.";
    private SpeakerLayout layout = SpeakerLayout.Surround51;
    private int sampleRate = 48000;
    private double masterGainDb = -6;
    private TrackViewModel? selected;
    private bool busy, dirty;
    private string? sessionPath;
    private WasapiPlayback? player;
    private StreamingMixer? liveMixer;
    private AudioDevice? selectedOutput;
    private bool stereoMonitor = true, closed;
    private double monitorVolume = .5;
    private Task playbackStopped = Task.CompletedTask;
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private long meterFrames = -1;
    private SampleFormat exportFormat = SampleFormat.Pcm24;
    private bool exportDither = true, exportBroadcast = true;
    private string momentaryText = "M   —", shortTermText = "S   —", integratedText = "I   — LUFS", truePeakText = "TP  — dBTP";

    public string SessionName { get => sessionName; set { sessionName = value; Changed(); Edit(audio: false); } }
    public SpeakerLayout Layout { get => layout; set { layout = value; Changed(); Changed(nameof(OutputLabels)); Changed(nameof(HasLfe)); Edit(restart: true); ResetMeters(); } }
    public int SampleRate { get => sampleRate; set { sampleRate = value; Changed(); Edit(restart: true); } }
    public double MasterGainDb { get => masterGainDb; set { masterGainDb = value; Changed(); Edit(); } }
    public TrackViewModel? Selected { get => selected; set { selected = value; Changed(); Changed(nameof(CanEditSelected)); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string RenderSummary { get => renderSummary; private set { renderSummary = value; Changed(); } }
    public bool CanEdit => !busy;
    public bool CanRender => !busy && Tracks.Count > 0;
    public bool CanPlay => CanRender && SelectedOutput is not null;
    public AudioDevice? SelectedOutput { get => selectedOutput; set { if (selectedOutput == value) return; StopPlayback(); selectedOutput = value; Changed(); Changed(nameof(CanPlay)); } }
    public bool StereoMonitor { get => stereoMonitor; set { if (stereoMonitor == value) return; StopPlayback(); stereoMonitor = value; Changed(); ResetMeters(); } }
    public double MonitorVolume { get => monitorVolume; set { monitorVolume = Math.Clamp(value, 0, 1); if (player is not null) player.Volume = monitorVolume; Changed(); } }
    public bool CanEditSelected => !busy && Selected is not null;
    public bool HasLfe => Layout != SpeakerLayout.Stereo;
    public string OutputLabels => string.Join(" / ", SpatialMixer.Speakers(Layout).Select(s => s.Name));
    public string TrackSummary => $"{Tracks.Count} / 32 tracks · media stays linked";
    public FormatOption[] Formats { get; } = [new(SampleFormat.Pcm24, "PCM 24-bit"), new(SampleFormat.Pcm16, "PCM 16-bit"), new(SampleFormat.Float32, "Float 32-bit")];
    public SampleFormat ExportFormat { get => exportFormat; set { exportFormat = value; Changed(); Changed(nameof(CanDither)); } }
    public bool ExportDither { get => exportDither; set { exportDither = value; Changed(); } }
    public bool ExportBroadcast { get => exportBroadcast; set { exportBroadcast = value; Changed(); } }
    public bool CanDither => CanEdit && ExportFormat != SampleFormat.Float32;
    public string MomentaryText { get => momentaryText; private set { momentaryText = value; Changed(); } }
    public string ShortTermText { get => shortTermText; private set { shortTermText = value; Changed(); } }
    public string IntegratedText { get => integratedText; private set { integratedText = value; Changed(); } }
    public string TruePeakText { get => truePeakText; private set { truePeakText = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        meterTimer.Tick += MeterTick;
        Loaded += async (_, _) =>
        {
            ResetMeters(); meterTimer.Start();
            await RefreshOutputsAsync();
            await OpenFilesAsync(Environment.GetCommandLineArgs().Skip(1).ToArray());
        };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && CanEdit ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await OpenFilesAsync(files); };
    }

    // A session file replaces the session (after the unsaved-changes prompt); WAV files are imported as tracks.
    private async Task OpenFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || busy) return;
        var session = files.FirstOrDefault(f => f.EndsWith(".xsurlab.json", StringComparison.OrdinalIgnoreCase));
        if (session is not null) { if (ConfirmDiscard()) await OpenAsync(session); return; }
        var audio = files.Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (audio.Length > 0) await ImportAsync(audio);
        else Status = "Drop WAV files or an .xsurlab.json session.";
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Edit(bool restart = false, bool audio = true)
    {
        dirty = true;
        if (player is not null && audio)
        {
            if (restart) { StopPlayback(); Status = "Playback stopped · play again after changing the session format or offset."; }
            else
            {
                try { liveMixer?.Update(Snapshot()); }
                catch (Exception ex) { StopPlayback(); Status = ex.Message; }
            }
        }
        RenderSummary = "Settings changed · render to measure sample peak.";
        Title = $"XSurLab · {SessionName} *";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        Changed(nameof(CanEdit)); Changed(nameof(CanRender)); Changed(nameof(CanPlay)); Changed(nameof(CanEditSelected)); Changed(nameof(CanDither));
    }
    private void TrackChanged(object? sender, PropertyChangedEventArgs e) => Edit(restart: e.PropertyName == nameof(TrackViewModel.StartSeconds), audio: e.PropertyName != nameof(TrackViewModel.Name));
    private void RefreshTracks()
    {
        Changed(nameof(TrackSummary)); Changed(nameof(CanRender)); Changed(nameof(CanPlay)); Changed(nameof(CanEditSelected));
    }
    private void Add(TrackViewModel track) { track.PropertyChanged += TrackChanged; Tracks.Add(track); }
    private void ClearTracks()
    {
        foreach (var track in Tracks) track.PropertyChanged -= TrackChanged;
        Tracks.Clear(); Selected = null;
    }
    private SessionDocument Snapshot() => new()
    {
        Name = SessionName, Layout = Layout, SampleRate = SampleRate, MasterGainDb = MasterGainDb,
        Tracks = Tracks.Select(t => t.Snapshot).ToList()
    };
    private bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "Save this session before continuing?", "XSurLab",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SaveSession());
    }
    private void Saved()
    {
        dirty = false;
        Title = $"XSurLab · {SessionName}";
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "WAV audio|*.wav", Multiselect = true, Title = "Import mono / stereo PCM WAV" };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync(dialog.FileNames);
    }
    private async Task ImportAsync(IReadOnlyList<string> files)
    {
        if (busy) return;
        StopPlayback(); SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            if (Tracks.Count + files.Count > 32) throw new InvalidDataException("A session supports up to 32 tracks.");
            var token = operation.Token;
            long used = Tracks.Sum(t => (long)t.Audio.Samples.Length * 4);
            Status = "Reading audio…";
            var loaded = await Task.Run(() => LoadMedia(files.Select(p => new TrackSettings
            {
                Name = Path.GetFileNameWithoutExtension(p), MediaPath = Path.GetFullPath(p)
            }), used, token), token);
            foreach (var track in loaded) Add(track);
            Selected = loaded.FirstOrDefault();
            Edit(); Status = $"Imported {loaded.Count} track(s)."; RefreshTracks();
        }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (Exception ex) { Error(ex); }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }

    private static List<TrackViewModel> LoadMedia(IEnumerable<TrackSettings> settings, long used = 0, CancellationToken cancellationToken = default)
    {
        List<TrackViewModel> loaded = [];
        foreach (var item in settings)
        {
            AudioBuffer audio;
            try { audio = WaveReader.Read(item.MediaPath, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"Cannot import '{item.MediaPath}': {ex.Message}", ex); }
            used += (long)audio.Samples.Length * 4;
            if (used > 256L * 1024 * 1024) throw new InvalidDataException("Decoded session audio exceeds 256 MiB. Use shorter clips.");
            loaded.Add(new(item, audio));
        }
        return loaded;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "XSurLab session|*.xsurlab.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await OpenAsync(dialog.FileName);
    }
    private async Task OpenAsync(string file)
    {
        if (busy) return;
        StopPlayback(); SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            var token = operation.Token;
            var document = await Task.Run(() => SessionStore.Load(file), token);
            var loaded = await Task.Run(() => LoadMedia(document.Tracks, cancellationToken: token), token);
            ClearTracks();
            SessionName = document.Name; Layout = document.Layout; SampleRate = document.SampleRate; MasterGainDb = document.MasterGainDb;
            foreach (var track in loaded) Add(track);
            Selected = Tracks.FirstOrDefault(); sessionPath = Path.GetFullPath(file);
            Saved(); RefreshTracks(); Status = "Session opened · all media resolved.";
        }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (Exception ex) { Error(ex); }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        StopPlayback(); ClearTracks(); sessionPath = null;
        SessionName = "Untitled session"; Layout = SpeakerLayout.Surround51; SampleRate = 48000; MasterGainDb = -6;
        Saved(); RefreshTracks(); Status = "New session · import WAV to begin.";
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveSession();
    private bool SaveSession()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "XSurLab session|*.xsurlab.json", DefaultExt = ".xsurlab.json",
                FileName = sessionPath ?? "Untitled.xsurlab.json"
            };
            if (dialog.ShowDialog(this) != true) return false;
            SessionStore.Save(dialog.FileName, Snapshot()); sessionPath = dialog.FileName; Saved();
            Status = "Session saved · media linked by relative path."; return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } track) return;
        StopPlayback(); track.PropertyChanged -= TrackChanged; Tracks.Remove(track);
        Selected = Tracks.FirstOrDefault(); Edit(); RefreshTracks(); Status = "Track removed · source file preserved.";
    }

    private async void RefreshOutputs_Click(object sender, RoutedEventArgs e) => await RefreshOutputsAsync();
    private async Task RefreshOutputsAsync()
    {
        StopPlayback(); SetBusy(true);
        try
        {
            var previous = SelectedOutput?.Id;
            var devices = await WasapiPlayback.EnumerateAsync();
            if (closed) return;
            OutputDevices.Clear();
            foreach (var device in devices) OutputDevices.Add(device);
            SelectedOutput = OutputDevices.FirstOrDefault(d => d.Id == previous) ?? OutputDevices.FirstOrDefault();
            Status = devices.Count == 0 ? "No active output devices · connect a device, then refresh outputs."
                : $"{devices.Count} WASAPI output(s) · stereo fold-down enabled by default.";
        }
        catch (Exception ex) { if (!closed) { SelectedOutput = null; Status = "Cannot enumerate WASAPI outputs: " + ex.Message; } }
        finally { if (!closed) SetBusy(false); }
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPlay) return;
        StopPlayback(); SetBusy(true);
        WasapiPlayback? media = null;
        try
        {
            await playbackStopped;
            if (closed) return;
            var document = Snapshot();
            liveMixer = new StreamingMixer(document, Tracks.Select(t => new LoadedTrack(t.Snapshot, t.Audio)).ToArray(), StereoMonitor);
            ResetMeters(); meterFrames = -1;
            var started = new Progress<PlaybackInfo>(info =>
            {
                if (player != media || closed) return;
                Status = $"Playing {(StereoMonitor ? "stereo fold-down" : "session channels")} · {info.DeviceName} · {info.Channels} ch / {info.SampleRate / 1000d:0.#} kHz · {info.BufferMilliseconds:F0} ms buffer";
            });
            media = new WasapiPlayback(liveMixer, SelectedOutput!.Id, MonitorVolume, started);
            player = media;
            Status = "Opening WASAPI monitor…";
        }
        catch (Exception ex) { StopPlayback(); if (!closed) Error(ex); }
        finally { if (!closed) SetBusy(false); }
        if (media is null) return;
        try
        {
            await media.Completion;
            if (player != media || closed) return;
            var report = liveMixer!.Meter.Report();
            RenderSummary = $"Last monitor · {Lufs(report.Integrated)} LUFS · LRA {report.Range:F1} LU · {Db(report.TruePeakDb)} dBTP · before monitor level";
            StopPlayback(); Status = "Playback finished.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (player == media && !closed) { StopPlayback(); Error(ex); } }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRender) return;
        var dialog = new SaveFileDialog { Filter = "WAV audio|*.wav", DefaultExt = ".wav", FileName = "XSurLab-mix.wav" };
        if (dialog.ShowDialog(this) != true) return;
        StopPlayback();
        if (sessionPath is not null && string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(sessionPath), StringComparison.OrdinalIgnoreCase))
        { Error(new IOException("Choose an audio filename different from the session file.")); return; }
        var result = await RenderAsync(dialog.FileName);
        if (result is not null) Status = $"Exported {result.Channels}-channel PCM · {Path.GetFileName(dialog.FileName)}";
    }
    private async Task<RenderResult?> RenderAsync(string path)
    {
        SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            var document = Snapshot();
            var media = Tracks.Select(t => new LoadedTrack(t.Snapshot, t.Audio)).ToArray();
            var token = operation.Token;
            Status = "Rendering PCM WAV…";
            var progress = new Progress<double>(p => Status = $"Rendering PCM WAV · {p:P0}");
            var options = new ExportOptions { Format = ExportFormat, Dither = ExportDither, BroadcastWave = ExportBroadcast };
            var result = await Task.Run(() => WaveRenderer.Render(path, document, media, options, token, progress), token);
            var loudness = result.Loudness;
            RenderSummary = $"Last render · {result.Duration:F2} s · {result.SampleRate / 1000d:0.#} kHz · {result.Channels} ch · "
                + $"{Lufs(loudness.Integrated)} LUFS · LRA {loudness.Range:F1} LU · {Db(loudness.TruePeakDb)} dBTP · sample peak {Db(result.PeakDb)} dBFS";
            ResetMeters(); Meters.Show(loudness.ChannelPeaks);
            return result;
        }
        catch (OperationCanceledException) { Status = "Render cancelled · destination preserved."; return null; }
        catch (Exception ex) { Error(ex); return null; }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        operation?.Cancel(); StopPlayback();
        Status = busy ? "Cancelling work…" : "Stopped.";
    }
    private void StopPlayback()
    {
        var previous = player;
        player = null; liveMixer = null;
        if (previous is null) return;
        previous.Dispose();
        playbackStopped = previous.Completion.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
    }
    private void Error(Exception ex)
    {
        Status = ex.Message;
        MessageBox.Show(this, ex.Message, "XSurLab", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { operation?.Cancel(); e.Cancel = true; Status = "Cancelling work · close again when finished."; return; }
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        closed = true; meterTimer.Stop(); StopPlayback();
    }

    private void ResetMeters() => Meters.SetChannels(liveMixer is { StereoMonitor: true }
        ? ["L", "R"] : SpatialMixer.Speakers(Layout).Select(s => s.Name).ToArray());
    private void MeterTick(object? sender, EventArgs e)
    {
        var reading = liveMixer?.Meter.Latest;
        if (reading is null || reading.Frames == meterFrames) { Meters.Show(null); return; }
        meterFrames = reading.Frames;
        Meters.Show(reading.ChannelPeaks);
        MomentaryText = $"M   {Lufs(reading.Momentary)}";
        ShortTermText = $"S   {Lufs(reading.ShortTerm)}";
        IntegratedText = $"I   {Lufs(reading.Integrated)} LUFS";
        TruePeakText = $"TP  {Db(reading.TruePeakDb)} dBTP";
    }
    private static string Lufs(double value) => double.IsFinite(value) ? value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "—";
    private static string Db(double value) => double.IsFinite(value) ? value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "−∞";

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            RoutedEventHandler? action = e.Key switch
            {
                Key.N => New_Click, Key.O => Open_Click, Key.S => Save_Click, Key.I => Import_Click, Key.E => Export_Click, _ => null
            };
            if (action is null || busy) return;
            e.Handled = true; action(this, new RoutedEventArgs());
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape) { e.Handled = true; Stop_Click(this, new RoutedEventArgs()); }
        // Space is the transport key everywhere except where it types or toggles.
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBoxBase or ToggleButton))
        {
            e.Handled = true;
            if (player is not null) Stop_Click(this, new RoutedEventArgs());
            else if (CanPlay) Play_Click(this, new RoutedEventArgs());
        }
    }
}

public sealed record LayoutOption(SpeakerLayout Layout, string Name);
public sealed record FormatOption(SampleFormat Format, string Name);




