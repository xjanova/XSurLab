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
    public LayoutOption[] Layouts { get; } = [new(SpeakerLayout.Stereo, "Stereo 2.0"), new(SpeakerLayout.Surround51, "5.1 Side"), new(SpeakerLayout.Surround51Back, "5.1 Back"), new(SpeakerLayout.Surround71, "7.1"), new(SpeakerLayout.Surround514, "5.1.4"), new(SpeakerLayout.Surround714, "7.1.4")];
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
    private OutputMode outputMode = OutputMode.Shared;
    private double monitorVolume = .5;
    // Windows reports one plug or unplug as a burst of notifications; the list is enumerated once the burst settles.
    private DeviceWatcher? watcher;
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool syncingOutputs, outputSyncRunning, outputSyncAgain, outputSyncManual;
    private Task playbackStopped = Task.CompletedTask;
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private long meterFrames = -1;
    private SampleFormat exportFormat = SampleFormat.Pcm24;
    private bool exportDither = true, exportBroadcast = true;
    private bool showEq;
    private string? lastDelivery;
    private int eqBand = 2;
    private string momentaryText = "M   —", shortTermText = "S   —", integratedText = "I   — LUFS", truePeakText = "TP  — dBTP";
    private double playhead;
    private long playFrom;
    // Undo keeps whole-session snapshots. Continuous edits of one control (a slider drag, typing) become one step.
    private readonly EditHistory<SessionState> history;
    private readonly DispatcherTimer commitTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private PendingEdit? pending;
    private SessionState? savedState;
    private bool restoring;

    public string SessionName { get => sessionName; set { sessionName = value; Changed(); Edit("session-name", "session name", audio: false); } }
    public SpeakerLayout Layout { get => layout; set { layout = value; Changed(); Changed(nameof(OutputLabels)); Changed(nameof(HasLfe)); Changed(nameof(HeightHint)); Changed(nameof(CanSendLfe)); Edit("layout", "speaker layout", restart: true, commit: true); ResetMeters(); } }
    public int SampleRate { get => sampleRate; set { sampleRate = value; Changed(); Edit("rate", "sample rate", restart: true, commit: true); } }
    public double MasterGainDb { get => masterGainDb; set { masterGainDb = value; Changed(); Edit("master", "master gain"); } }
    /// <summary>Seconds from the session start. Setting it while playing seeks the monitor.</summary>
    public double PlayheadSeconds
    {
        get => playhead;
        set
        {
            ShowPlayhead(double.IsFinite(value) ? value : 0);
            if (liveMixer is { } mixer && player is not null)
            {
                // The end of playback returns here, to the last place the user chose.
                playFrom = Math.Min((long)Math.Round(playhead * mixer.SampleRate), mixer.TotalFrames);
                mixer.Seek(playFrom);
            }
        }
    }
    public string PlayheadText => $"{TimelineView.Clock(playhead)} / {TimelineView.Clock(TimelineView.End(Tracks))}";
    public bool CanUndo => CanEdit && (pending is not null || history.CanUndo);
    public bool CanRedo => CanEdit && pending is null && history.CanRedo;
    public string UndoText => CanUndo ? $"Undo {pending?.Label ?? history.UndoLabel} · Ctrl+Z" : "Nothing to undo";
    public string RedoText => CanRedo ? $"Redo {history.RedoLabel} · Ctrl+Y" : "Nothing to redo";
    public TrackViewModel? Selected { get => selected; set { selected = value; Changed(); Changed(nameof(CanEditSelected)); Changed(nameof(CanPanSelected)); Changed(nameof(CanSendLfe)); BandChanged(); } }
    public bool CanPanSelected => CanEditSelected && Selected?.CanPan == true;
    public bool CanSendLfe => CanPanSelected && HasLfe;
    public string Status { get => status; private set { status = value; Changed(); } }
    public string RenderSummary { get => renderSummary; private set { renderSummary = value; Changed(); } }
    public bool CanEdit => !busy;
    public bool CanRender => !busy && Tracks.Count > 0;
    public bool CanPlay => CanRender && SelectedOutput is not null;
    // While the device list updates itself, the selection is put back afterwards and playback continues if its output is still there.
    public AudioDevice? SelectedOutput { get => selectedOutput; set { if (selectedOutput == value) return; if (!syncingOutputs) StopPlayback(); selectedOutput = value; Changed(); Changed(nameof(CanPlay)); } }
    public ModeOption[] OutputModes { get; } = [new(OutputMode.Shared, "Shared"), new(OutputMode.Exclusive, "Exclusive")];
    public OutputMode OutputMode
    {
        get => outputMode;
        set
        {
            if (outputMode == value) return;
            StopPlayback(); outputMode = value; Changed();
            Status = value == OutputMode.Exclusive ? "Exclusive · Windows volume and effects do not apply · set MONITOR before you play."
                : "Shared · through the Windows mixer, volume and effects.";
        }
    }
    public bool StereoMonitor { get => stereoMonitor; set { if (stereoMonitor == value) return; StopPlayback(); stereoMonitor = value; Changed(); ResetMeters(); } }
    public double MonitorVolume { get => monitorVolume; set { monitorVolume = Math.Clamp(value, 0, 1); if (player is not null) player.Volume = monitorVolume; Changed(); } }
    public bool CanEditSelected => !busy && Selected is not null;
    public bool HasLfe => Layout != SpeakerLayout.Stereo;
    public string HeightHint => SpatialMixer.HasHeights(Layout) ? "90° = overhead · drag toward the centre to lift" : "Heard on 5.1.4 and 7.1.4 only";
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
    public bool ShowEq { get => showEq; set { if (showEq == value) return; showEq = value; Changed(); Changed(nameof(ShowSpatial)); } }
    public bool ShowSpatial { get => !showEq; set => ShowEq = !value; }
    public string[] EqBandNames => TrackEq.Names;
    public int EqBandIndex { get => eqBand; set { eqBand = Math.Clamp(value, 0, TrackEq.Count - 1); BandChanged(); } }
    private EqBand Band => (Selected?.Eq ?? TrackEq.Flat).Band(eqBand);
    // Moving a control switches its band on, so what is adjusted is what is heard.
    private void SetBand(EqBand band) { if (Selected is { } track && track.Eq.Band(eqBand) != band) track.Eq = track.Eq.WithBand(eqBand, band); }
    public bool BandOn { get => Band.On; set => SetBand(Band with { On = value }); }
    public double BandFrequencyLog { get => Math.Log10(Band.Frequency); set => SetBand(Band with { On = true, Frequency = EqView.Nice(Math.Pow(10, value)) }); }
    public double BandGainDb { get => Band.GainDb; set => SetBand(Band with { On = true, GainDb = Math.Clamp(Math.Round(value * 2) / 2, -24, 24) }); }
    public double BandQLog { get => Math.Log10(Band.Q); set => SetBand(Band with { On = true, Q = Math.Clamp(Math.Round(Math.Pow(10, value), 2), .1, 10) }); }
    public bool BandHasGain => TrackEq.HasGain(eqBand);
    public bool BandHasQ => TrackEq.HasQ(eqBand);
    public string BandText => $"{EqView.Hz(Band.Frequency)}{(BandHasGain ? $" · {Band.GainDb:+0.0;−0.0;0.0} dB" : "")}{(BandHasQ ? $" · Q {Band.Q:0.00}" : "")}";
    private void BandChanged()
    {
        foreach (var name in new[] { nameof(EqBandIndex), nameof(BandOn), nameof(BandFrequencyLog), nameof(BandGainDb), nameof(BandQLog), nameof(BandHasGain), nameof(BandHasQ), nameof(BandText) })
            Changed(name);
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        history = new(Capture(), SameState.Instance);
        savedState = history.Current;
        commitTimer.Tick += (_, _) => Flush();
        InitializeComponent();
        DataContext = this;
        meterTimer.Tick += MeterTick;
        deviceTimer.Tick += async (_, _) => { deviceTimer.Stop(); await SyncOutputsAsync(manual: false); };
        Loaded += async (_, _) =>
        {
            ResetMeters(); meterTimer.Start();
            // Watching starts before the first enumeration so a device plugged in meanwhile is not missed.
            try
            {
                watcher = new DeviceWatcher();
                watcher.Changed += (_, _) => Dispatcher.InvokeAsync(ScheduleOutputSync);
            }
            catch (Exception ex) { watcher = null; Status = "The output list will not follow devices by itself · use Refresh outputs · " + ex.Message; }
            await SyncOutputsAsync(manual: true);
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
    /// <param name="key">Edits with the same key in a row merge into one undo step until a pause or another edit.</param>
    /// <param name="commit">Records the undo step at once, for discrete edits such as mute or a clip move.</param>
    private void Edit(string key, string label, bool restart = false, bool audio = true, bool commit = false)
    {
        if (restoring) return;
        if (pending is { } previous && previous.Key != key) history.Record(previous.State, previous.Label);
        pending = new(Capture(), label, key);
        commitTimer.Stop();
        if (commit) Flush(); else { commitTimer.Start(); UndoChanged(); }
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
        Changed(nameof(CanPanSelected)); Changed(nameof(CanSendLfe)); UndoChanged();
    }
    private void TrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TrackViewModel track || e.PropertyName is nameof(TrackViewModel.Summary) or "" or null) return;
        string property = e.PropertyName;
        Edit($"{track.Snapshot.Id}:{property}", $"{Describe(property)} · {track.Name}",
            restart: property == nameof(TrackViewModel.StartSeconds),
            audio: property is not (nameof(TrackViewModel.Name) or nameof(TrackViewModel.Group)),
            commit: property is nameof(TrackViewModel.Mute) or nameof(TrackViewModel.Solo) or nameof(TrackViewModel.StartSeconds) or nameof(TrackViewModel.Group));
        if (property == nameof(TrackViewModel.StartSeconds)) Changed(nameof(PlayheadText));
        if (property == nameof(TrackViewModel.Eq) && ReferenceEquals(sender, Selected)) BandChanged();
    }
    private static string Describe(string property) => property switch
    {
        nameof(TrackViewModel.GainDb) => "gain", nameof(TrackViewModel.Azimuth) => "azimuth", nameof(TrackViewModel.Elevation) => "elevation",
        nameof(TrackViewModel.Spread) => "spread", nameof(TrackViewModel.Eq) => "EQ", nameof(TrackViewModel.LfeDb) => "LFE send",
        nameof(TrackViewModel.StartSeconds) => "start", nameof(TrackViewModel.Mute) => "mute", nameof(TrackViewModel.Solo) => "solo",
        nameof(TrackViewModel.Name) => "track name", nameof(TrackViewModel.Group) => "group", _ => "track"
    };
    private void RefreshTracks()
    {
        Changed(nameof(TrackSummary)); Changed(nameof(CanRender)); Changed(nameof(CanPlay)); Changed(nameof(CanEditSelected));
        ShowPlayhead(playhead);
    }
    // Moves the playhead without seeking: following playback, or keeping it inside the session after an edit.
    private void ShowPlayhead(double seconds)
    {
        playhead = Math.Clamp(seconds, 0, TimelineView.End(Tracks));
        Changed(nameof(PlayheadSeconds)); Changed(nameof(PlayheadText));
    }

    private SessionState Capture() => new(Snapshot(), Tracks.Select(t => t.Audio).ToArray());
    private void UndoChanged() { Changed(nameof(CanUndo)); Changed(nameof(CanRedo)); Changed(nameof(UndoText)); Changed(nameof(RedoText)); }
    private void Flush()
    {
        commitTimer.Stop();
        if (pending is { } edit) { pending = null; history.Record(edit.State, edit.Label); }
        UndoChanged();
    }
    private void ResetHistory()
    {
        commitTimer.Stop(); pending = null;
        history.Reset(Capture()); savedState = history.Current;
        UndoChanged();
    }
    private void Undo()
    {
        if (busy) return;
        Flush();
        if (!history.CanUndo) return;
        string label = history.UndoLabel!;
        Apply(history.Undo());
        Status = $"Undid {label}.";
    }
    private void Redo()
    {
        if (busy) return;
        Flush();
        if (!history.CanRedo) return;
        Apply(history.Redo());
        Status = $"Redid {history.UndoLabel}.";
    }
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    // Puts a snapshot back. Tracks keep their view model (and selection) when they still exist; removed ones come back with their media.
    private void Apply(SessionState state)
    {
        var document = state.Document;
        var before = Snapshot();
        bool restart = document.Layout != before.Layout || document.SampleRate != before.SampleRate || document.Tracks.Count != before.Tracks.Count
            || document.Tracks.Zip(before.Tracks).Any(p => p.First.Id != p.Second.Id || p.First.StartSeconds != p.Second.StartSeconds);
        var selectedId = Selected?.Snapshot.Id;
        restoring = true;
        try
        {
            SessionName = document.Name; Layout = document.Layout; SampleRate = document.SampleRate; MasterGainDb = document.MasterGainDb;
            var existing = Tracks.ToDictionary(t => t.Snapshot.Id);
            var wanted = new List<TrackViewModel>();
            TrackViewModel? returned = null;
            for (int i = 0; i < document.Tracks.Count; i++)
            {
                var settings = document.Tracks[i];
                if (existing.Remove(settings.Id, out var track)) track.Restore(settings);
                else { track = new TrackViewModel(settings, state.Media[i]); returned ??= track; }
                wanted.Add(track);
            }
            // A track that comes back is selected, so the undo is visible where it happened.
            if (returned is not null) selectedId = returned.Snapshot.Id;
            foreach (var gone in existing.Values) { gone.PropertyChanged -= TrackChanged; Tracks.Remove(gone); }
            for (int i = 0; i < wanted.Count; i++)
            {
                int at = Tracks.IndexOf(wanted[i]);
                if (at < 0) { wanted[i].PropertyChanged += TrackChanged; Tracks.Insert(i, wanted[i]); }
                else if (at != i) Tracks.Move(at, i);
            }
            Selected = Tracks.FirstOrDefault(t => t.Snapshot.Id == selectedId) ?? Tracks.FirstOrDefault();
        }
        finally { restoring = false; }
        if (player is not null)
        {
            if (restart) { StopPlayback(); Status = "Playback stopped · the undone edit changed media, offsets, layout or rate."; }
            else
            {
                try { liveMixer?.Update(Snapshot()); }
                catch (Exception ex) { StopPlayback(); Status = ex.Message; }
            }
        }
        dirty = !ReferenceEquals(history.Current, savedState);
        Title = $"XSurLab · {SessionName}{(dirty ? " *" : "")}";
        RenderSummary = "Settings changed · render to measure sample peak.";
        RefreshTracks(); BandChanged(); UndoChanged();
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
        Flush(); savedState = history.Current;
        dirty = false;
        Title = $"XSurLab · {SessionName}";
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "WAV audio|*.wav", Multiselect = true, Title = "Import WAV · mono, stereo or multichannel bed" };
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
            long used = Tracks.Sum(t => t.Audio.MemoryBytes);
            Status = "Reading audio…";
            var loaded = await Task.Run(() => LoadMedia(files.Select(p => new TrackSettings
            {
                Name = Path.GetFileNameWithoutExtension(p), MediaPath = Path.GetFullPath(p)
            }), used, token), token);
            foreach (var track in loaded) Add(track);
            Selected = loaded.FirstOrDefault();
            Edit("import", loaded.Count == 1 ? $"import · {loaded[0].Name}" : $"import of {loaded.Count} tracks", commit: true);
            Status = $"Imported {loaded.Count} track(s)."; RefreshTracks();
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
            // Files above WaveReader.StreamThreshold stream from disk and do not count; this bounds the small decoded ones.
            used += audio.MemoryBytes;
            if (used > 1L << 30) throw new InvalidDataException("Decoded session audio exceeds 1 GiB.");
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
            ShowPlayhead(0); ResetHistory(); Saved(); RefreshTracks(); Status = "Session opened · all media resolved.";
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
        ShowPlayhead(0); ResetHistory(); Saved(); RefreshTracks(); Status = "New session · import WAV to begin.";
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
        Selected = Tracks.FirstOrDefault(); Edit("remove", $"removal · {track.Name}", commit: true); RefreshTracks();
        Status = "Track removed · source file preserved · Ctrl+Z brings it back.";
    }

    private async void RefreshOutputs_Click(object sender, RoutedEventArgs e) => await SyncOutputsAsync(manual: true);
    private void ScheduleOutputSync()
    {
        if (closed) return;
        deviceTimer.Stop(); deviceTimer.Start();
    }
    // One enumeration at a time; a request that arrives meanwhile runs once more afterwards.
    private async Task SyncOutputsAsync(bool manual)
    {
        outputSyncManual |= manual;
        if (outputSyncRunning) { outputSyncAgain = true; return; }
        outputSyncRunning = true;
        try
        {
            do
            {
                outputSyncAgain = false;
                bool announce = outputSyncManual;
                outputSyncManual = false;
                IReadOnlyList<AudioDevice> devices;
                try { devices = await WasapiPlayback.EnumerateAsync(); }
                catch (Exception ex) { if (!closed) Status = "Cannot enumerate WASAPI outputs: " + ex.Message; return; }
                if (closed) return;
                ApplyOutputs(devices, announce);
            }
            while (outputSyncAgain && !closed);
        }
        finally { outputSyncRunning = false; }
    }
    private void ApplyOutputs(IReadOnlyList<AudioDevice> devices, bool announce)
    {
        var previous = SelectedOutput;
        var before = OutputDevices.ToArray();
        syncingOutputs = true;
        try
        {
            DeviceList.Sync(OutputDevices, devices);
            SelectedOutput = OutputDevices.FirstOrDefault(d => d.Id == previous?.Id) ?? OutputDevices.FirstOrDefault();
        }
        finally { syncingOutputs = false; }
        var now = SelectedOutput;
        if (previous is not null && now?.Id != previous.Id)
        {
            // The output in use is gone. Nothing starts playing on the replacement by itself.
            bool stopped = player is not null;
            StopPlayback();
            Status = $"Output removed · {previous.Name}{(stopped ? " · playback stopped" : "")}{(now is null ? " · no output left" : $" · now {now.Name}")}";
            return;
        }
        if (announce)
        {
            Status = devices.Count == 0 ? "No active output devices · connect one and the list updates by itself."
                : $"{devices.Count} WASAPI output(s) · the list follows devices as they are plugged in or removed.";
            return;
        }
        var added = devices.Where(d => before.All(b => b.Id != d.Id)).Select(d => d.Name).ToArray();
        var removed = before.Where(b => devices.All(d => d.Id != b.Id)).Select(b => b.Name).ToArray();
        if (added.Length > 0) Status = "Output added · " + string.Join(", ", added);
        else if (removed.Length > 0) Status = "Output removed · " + string.Join(", ", removed);
        else if (now is not null && previous is not null && now != previous) Status = "Output format changed · " + now.DisplayName;
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
            // The output may have been unplugged while the previous stream was releasing.
            if (SelectedOutput is not { } output) { Status = "Choose an output first."; return; }
            var document = Snapshot();
            liveMixer = new StreamingMixer(document, Tracks.Select(t => new LoadedTrack(t.Snapshot, t.Audio)).ToArray(), StereoMonitor);
            // Play from the playhead; from the end it starts over.
            playFrom = (long)Math.Round(playhead * liveMixer.SampleRate);
            if (playFrom >= liveMixer.TotalFrames) { playFrom = 0; ShowPlayhead(0); }
            if (playFrom > 0) liveMixer.Seek(playFrom);
            ResetMeters(); meterFrames = -1;
            var started = new Progress<PlaybackInfo>(info =>
            {
                if (player != media || closed) return;
                Status = $"Playing {(StereoMonitor ? "stereo fold-down" : "session channels")} · {info.Mode} {info.Format} · {info.DeviceName} · "
                    + $"{info.Channels} ch / {info.SampleRate / 1000d:0.#} kHz · {info.BufferMilliseconds:F0} ms {(info.Mode == OutputMode.Exclusive ? "period" : "buffer")}";
            });
            media = new WasapiPlayback(liveMixer, output.Id, MonitorVolume, started, OutputMode);
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
            var finished = liveMixer!;
            var report = finished.Meter.Report();
            RenderSummary = $"Last monitor · {Lufs(report.Integrated)} LUFS · LRA {report.Range:F1} LU · {Db(report.TruePeakDb)} dBTP · before monitor level";
            StopPlayback(); Status = "Playback finished · playhead back where playback started.";
            ShowPlayhead((double)playFrom / finished.SampleRate);
        }
        catch (OperationCanceledException) { }
        // An unplugged output is expected, not an error box: say so and let the list catch up.
        catch (AudioDeviceLostException ex) { if (player == media && !closed) { StopPlayback(); Status = ex.Message; ScheduleOutputSync(); } }
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
    private async void Deliver_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRender) return;
        StopPlayback();
        var export = new ExportOptions { Format = ExportFormat, Dither = ExportDither, BroadcastWave = ExportBroadcast };
        var document = Snapshot();
        string folder = sessionPath is not null ? Path.GetDirectoryName(Path.GetFullPath(sessionPath))! : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string suggested = lastDelivery ?? Path.Combine(folder, string.Concat(SessionName.Split(Path.GetInvalidFileNameChars())).Replace('.', '_').Trim() is { Length: > 0 } n ? n + ".wav" : "Mix.wav");
        var dialog = new DeliverWindow(document, suggested, export, Layouts.First(l => l.Layout == Layout).Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Options is not { } options) return;
        lastDelivery = dialog.MixPath;
        SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            var media = Tracks.Select(t => new LoadedTrack(t.Snapshot, t.Audio)).ToArray();
            var token = operation.Token;
            var progress = new Progress<double>(p => Status = $"Delivering · {p:P0}");
            Status = "Delivering…";
            var report = await Task.Run(() => Delivery.Deliver(dialog.MixPath, document, media, options, token, progress), token);
            var failed = report.Checks.Where(c => !c.Passed).ToArray();
            Status = $"Delivered {report.Files.Count} file(s) · QC {(report.Passed ? "PASS" : "FAIL")} · {Path.GetFileNameWithoutExtension(dialog.MixPath)}.qc.txt";
            RenderSummary = failed.Length == 0 ? "All QC checks passed." : "QC: " + string.Join(" · ", failed.Select(c => $"{c.Name} ({c.Detail})"));
            MessageBox.Show(this, string.Join("\n", report.Checks.Select(c => $"{(c.Passed ? "PASS" : "FAIL")}  {c.Name} · {c.Detail}"))
                + $"\n\n{report.Files.Count} file(s) in {Path.GetDirectoryName(dialog.MixPath)}",
                report.Passed ? "Delivery QC · PASS" : "Delivery QC · FAIL", MessageBoxButton.OK, report.Passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { Status = "Delivery cancelled · no file was replaced."; }
        catch (Exception ex) { Error(ex); }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
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
        // Stopping leaves the playhead where playback was heard, so Space continues from there.
        if (previous is not null && liveMixer is { SeekPending: false } mixer) ShowPlayhead(Audible(mixer, previous));
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
        closed = true; meterTimer.Stop(); commitTimer.Stop(); deviceTimer.Stop(); StopPlayback();
        watcher?.Dispose(); watcher = null;
    }

    private void ResetMeters() => Meters.SetChannels(liveMixer is { StereoMonitor: true }
        ? ["L", "R"] : SpatialMixer.Speakers(Layout).Select(s => s.Name).ToArray());
    private static double Audible(StreamingMixer mixer, WasapiPlayback playback) => (double)Math.Max(0, mixer.Position - playback.QueuedFrames) / mixer.SampleRate;
    private void MeterTick(object? sender, EventArgs e)
    {
        if (player is { } playback && liveMixer is { SeekPending: false } mixer) ShowPlayhead(Audible(mixer, playback));
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
        // Text boxes keep their own Ctrl+Z for typing; everywhere else it undoes session edits.
        var modifiers = Keyboard.Modifiers;
        if ((e.Key == Key.Z || e.Key == Key.Y) && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            && Keyboard.FocusedElement is not TextBoxBase)
        {
            e.Handled = true;
            if (e.Key == Key.Y || modifiers.HasFlag(ModifierKeys.Shift)) Redo(); else Undo();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            RoutedEventHandler? action = e.Key switch
            {
                Key.N => New_Click, Key.O => Open_Click, Key.S => Save_Click, Key.I => Import_Click, Key.E => Export_Click, Key.D => Deliver_Click, _ => null
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

// ToString is what screen readers announce for combo-box items.
public sealed record LayoutOption(SpeakerLayout Layout, string Name) { public override string ToString() => Name; }
public sealed record FormatOption(SampleFormat Format, string Name) { public override string ToString() => Name; }
public sealed record ModeOption(OutputMode Mode, string Name) { public override string ToString() => Name; }

/// <summary>An undo snapshot: the session plus the media of each track, so a removed track can come back.</summary>
public sealed record SessionState(SessionDocument Document, AudioBuffer[] Media);
internal sealed record PendingEdit(SessionState State, string Label, string Key);

internal sealed class SameState : IEqualityComparer<SessionState>
{
    public static readonly SameState Instance = new();
    private static readonly List<TrackSettings> None = [];
    // SessionDocument compares its track list by reference, so the tracks are compared item by item.
    public bool Equals(SessionState? a, SessionState? b) => ReferenceEquals(a, b) || a is not null && b is not null
        && a.Document with { Tracks = None } == b.Document with { Tracks = None } && a.Document.Tracks.SequenceEqual(b.Document.Tracks);
    public int GetHashCode(SessionState state) => state.Document.Tracks.Count;
}
