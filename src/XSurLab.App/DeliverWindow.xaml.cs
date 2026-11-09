using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using XSurLab.Core;

namespace XSurLab.App;

public sealed record TargetOption(LoudnessTarget Target, string Name) { public override string ToString() => Name; }

/// <summary>Chooses what a delivery writes. Existing files are replaced only after the user confirms it here.</summary>
public partial class DeliverWindow : Window, INotifyPropertyChanged
{
    // Remembered for the rest of the app session.
    private static bool lastMix = true, lastMono, lastStems;
    private static LoudnessTarget lastTarget;
    private readonly SessionDocument session;
    private readonly ExportOptions export;
    private string mixPath;
    private bool mix = lastMix, multiMono = lastMono, stems = lastStems;
    private LoudnessTarget target = lastTarget;
    private string[] planned = [];
    private string warning = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public TargetOption[] Targets { get; } =
    [
        new(LoudnessTarget.None, "None · report only"), new(LoudnessTarget.EbuR128, "EBU R128 · −23 LUFS, −1 dBTP"),
        new(LoudnessTarget.AtscA85, "ATSC A/85 · −24 LKFS, −2 dBTP"), new(LoudnessTarget.Streaming, "Streaming · −14 LUFS, −1 dBTP")
    ];
    public string MixPath { get => mixPath; set { mixPath = value; Changed(); Refresh(); } }
    public bool Mix { get => mix; set { mix = value; Changed(); Refresh(); } }
    public bool MultiMono { get => multiMono; set { multiMono = value; Changed(); Refresh(); } }
    public bool Stems { get => stems; set { stems = value; Changed(); Refresh(); } }
    public LoudnessTarget Target { get => target; set { target = value; Changed(); } }
    public string[] Planned { get => planned; private set { planned = value; Changed(); Changed(nameof(PlannedTitle)); } }
    public string PlannedTitle => $"FILES TO WRITE · {planned.Length}";
    public string Warning { get => warning; private set { warning = value; Changed(); } }
    public bool CanDeliver => (mix || multiMono || stems) && planned.Length > 0;
    // Check-box captions treat "_" as an access-key marker, so file names double it.
    public string MixLabel => Caption($"Interleaved mix · {BaseName}.wav");
    public string MonoLabel => Caption($"Multi-mono for encoders · {BaseName}.L.wav, {BaseName}.C.wav …");
    public string StemLabel => Caption($"Stems by track group · {string.Join(", ", session.Tracks.Select(Delivery.GroupOf).Distinct().Select(g => $"{BaseName}_{g}.wav"))}");
    private static string Caption(string text) => text.Replace("_", "__");
    public string FormatText { get; }
    public DeliveryOptions? Options { get; private set; }
    private string BaseName => Path.GetFileNameWithoutExtension(mixPath) is { Length: > 0 } name ? name : "Mix";

    public DeliverWindow(SessionDocument session, string suggestedPath, ExportOptions export, string layout)
    {
        this.session = session; this.export = export; mixPath = suggestedPath;
        FormatText = $"{export.Format switch { SampleFormat.Pcm16 => "PCM 16-bit", SampleFormat.Float32 => "Float 32-bit", _ => "PCM 24-bit" }}"
            + $"{(export.Dither && export.Format != SampleFormat.Float32 ? " · TPDF dither" : "")}{(export.BroadcastWave ? " · BWF" : "")} · {layout} · {session.SampleRate / 1000d:0.#} kHz";
        InitializeComponent();
        DataContext = this;
        Refresh();
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private void Refresh()
    {
        Changed(nameof(MixLabel)); Changed(nameof(MonoLabel)); Changed(nameof(StemLabel));
        try
        {
            var files = Delivery.PlannedFiles(mixPath, session, Build(false));
            Planned = files.Select(f => Path.GetFileName(f)).ToArray();
            int existing = files.Count(File.Exists);
            var notes = new List<string>();
            if (!Path.IsPathFullyQualified(mixPath)) notes.Add("Choose a full path for the mix file.");
            if (existing > 0) notes.Add($"{existing} file(s) from an earlier delivery will be replaced after you confirm.");
            if (session.Tracks.Any(t => t.Solo)) notes.Add("Solo is active: the mix will only contain soloed tracks (stems ignore solo). The QC report flags it.");
            Warning = string.Join("\n", notes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            Planned = []; Warning = "Choose a valid file name for the mix.";
        }
        Changed(nameof(CanDeliver));
    }

    private DeliveryOptions Build(bool overwrite) => new() { Export = export, Mix = mix, MultiMono = multiMono, Stems = stems, Target = target, Overwrite = overwrite };

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "WAV audio|*.wav", DefaultExt = ".wav", FileName = Path.GetFileName(mixPath), OverwritePrompt = false };
        try { dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(mixPath)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        if (dialog.ShowDialog(this) == true) MixPath = dialog.FileName;
    }

    private void Deliver_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDeliver || !Path.IsPathFullyQualified(mixPath)) { Warning = "Choose a full path for the mix file."; return; }
        var existing = Delivery.PlannedFiles(mixPath, session, Build(false)).Where(File.Exists).ToArray();
        if (existing.Length > 0 && MessageBox.Show(this,
                $"Replace {existing.Length} existing file(s)?\n\n{string.Join("\n", existing.Take(8).Select(Path.GetFileName))}{(existing.Length > 8 ? "\n…" : "")}",
                "Deliver", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        lastMix = mix; lastMono = multiMono; lastStems = stems; lastTarget = target;
        Options = Build(existing.Length > 0);
        DialogResult = true;
    }
}
