using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using XSurLab.Core;

namespace XSurLab.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public SpeakerLayout[] Layouts { get; } = Enum.GetValues<SpeakerLayout>();
    public int[] Rates { get; } = [44100, 48000, 96000];
    private string sessionName = "Untitled session", status = "Ready · import WAV to begin.", renderSummary = "Offline PCM mixing · stereo monitor uses the Windows default output.";
    private SpeakerLayout layout = SpeakerLayout.Surround51;
    private int sampleRate = 48000;
    private double masterGainDb = -6;
    private TrackViewModel? selected;
    private bool busy, dirty;
    private string? sessionPath, previewPath;
    private MediaPlayer? player;
    private CancellationTokenSource? operation;

    public string SessionName { get => sessionName; set { sessionName = value; Changed(); Edit(); } }
    public SpeakerLayout Layout { get => layout; set { layout = value; Changed(); Changed(nameof(OutputLabels)); Changed(nameof(HasLfe)); Edit(); } }
    public int SampleRate { get => sampleRate; set { sampleRate = value; Changed(); Edit(); } }
    public double MasterGainDb { get => masterGainDb; set { masterGainDb = value; Changed(); Edit(); } }
    public TrackViewModel? Selected { get => selected; set { selected = value; Changed(); Changed(nameof(CanEditSelected)); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string RenderSummary { get => renderSummary; private set { renderSummary = value; Changed(); } }
    public bool CanEdit => !busy;
    public bool CanRender => !busy && Tracks.Count > 0;
    public bool CanEditSelected => !busy && Selected is not null;
    public bool HasLfe => Layout != SpeakerLayout.Stereo;
    public string OutputLabels => string.Join(" / ", SpatialMixer.Speakers(Layout).Select(s => s.Name));
    public string TrackSummary => $"{Tracks.Count} / 32 tracks · media stays linked";
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Edit()
    {
        dirty = true;
        if (player is not null) { StopPlayback(); Status = "Mix changed · preview again to hear the new settings."; }
        RenderSummary = "Settings changed · render to measure sample peak.";
        Title = $"XSurLab · {SessionName} *";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        Changed(nameof(CanEdit)); Changed(nameof(CanRender)); Changed(nameof(CanEditSelected));
    }
    private void TrackChanged(object? sender, PropertyChangedEventArgs e) => Edit();
    private void RefreshTracks()
    {
        Changed(nameof(TrackSummary)); Changed(nameof(CanRender)); Changed(nameof(CanEditSelected));
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
        StopPlayback(); SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            if (Tracks.Count + dialog.FileNames.Length > 32) throw new InvalidDataException("A session supports up to 32 tracks.");
            var token = operation.Token;
            long used = Tracks.Sum(t => (long)t.Audio.Samples.Length * 4);
            Status = "Reading audio…";
            var loaded = await Task.Run(() => LoadMedia(dialog.FileNames.Select(p => new TrackSettings
            {
                Name = Path.GetFileNameWithoutExtension(p), MediaPath = p
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
        StopPlayback(); SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            var token = operation.Token;
            var document = await Task.Run(() => SessionStore.Load(dialog.FileName), token);
            var loaded = await Task.Run(() => LoadMedia(document.Tracks, cancellationToken: token), token);
            ClearTracks();
            SessionName = document.Name; Layout = document.Layout; SampleRate = document.SampleRate; MasterGainDb = document.MasterGainDb;
            foreach (var track in loaded) Add(track);
            Selected = Tracks.FirstOrDefault(); sessionPath = dialog.FileName;
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

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        string path = Path.Combine(Path.GetTempPath(), "XSurLab-preview-" + Guid.NewGuid().ToString("N") + ".wav");
        bool playing = false;
        try
        {
            if (await RenderAsync(path, true) is null) return;
            previewPath = path;
            var media = new MediaPlayer();
            player = media;
            media.Volume = .5;
            media.MediaOpened += (_, _) =>
            {
                if (player != media) return;
                media.Play();
                Status = "Playing stereo preview · Windows default output · monitor level 50%.";
            };
            media.MediaEnded += (_, _) => { if (player == media) { StopPlayback(); Status = "Preview finished."; } };
            media.MediaFailed += (_, args) => { if (player == media) { StopPlayback(); Error(args.ErrorException); } };
            media.Open(new Uri(path));
            playing = true;
            Status = "Opening stereo preview…";
        }
        catch (Exception ex) { StopPlayback(); Error(ex); }
        finally { if (!playing && File.Exists(path)) File.Delete(path); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "24-bit PCM WAV|*.wav", DefaultExt = ".wav", FileName = "XSurLab-mix.wav" };
        if (dialog.ShowDialog(this) != true) return;
        StopPlayback();
        if (sessionPath is not null && string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(sessionPath), StringComparison.OrdinalIgnoreCase))
        { Error(new IOException("Choose an audio filename different from the session file.")); return; }
        var result = await RenderAsync(dialog.FileName, false);
        if (result is not null) Status = $"Exported {result.Channels}-channel PCM · {Path.GetFileName(dialog.FileName)}";
    }
    private async Task<RenderResult?> RenderAsync(string path, bool monitor)
    {
        SetBusy(true); operation = new CancellationTokenSource();
        try
        {
            var document = Snapshot();
            var media = Tracks.Select(t => new LoadedTrack(t.Snapshot, t.Audio)).ToArray();
            var token = operation.Token;
            Status = monitor ? "Rendering stereo preview…" : "Rendering PCM WAV…";
            var progress = new Progress<double>(p => Status = $"Rendering {(monitor ? "stereo preview" : "PCM WAV")} · {p:P0}");
            var result = await Task.Run(() => WaveRenderer.Render(path, document, media, monitor, token, progress), token);
            RenderSummary = $"Last render · {result.Duration:F2} s · {result.SampleRate / 1000d:0.#} kHz · {result.Channels} ch · sample peak {result.PeakDb:F1} dBFS";
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
        player?.Close(); player = null;
        if (previewPath is { } path)
        {
            previewPath = null;
            try { File.Delete(path); }
            catch (IOException) { Status = "Preview stopped; Windows still holds its temporary file."; }
        }
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
        StopPlayback();
    }
}



