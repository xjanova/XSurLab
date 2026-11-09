using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XSurLab.App;

/// <summary>
/// Tracks as clips on one time axis. Click the ruler or an empty lane to place the playhead, drag a clip to move its
/// start (it snaps to zero, the playhead and other clip edges; hold Shift to move freely). The move is applied on release.
/// The playhead is drawn on its own visual so following playback does not redraw the clips.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(nameof(Tracks), typeof(IEnumerable), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((TimelineView)d).Subscribe(e.OldValue as IEnumerable, e.NewValue as IEnumerable)));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(TrackViewModel), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(nameof(Playhead), typeof(double), typeof(TimelineView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TimelineView)d).DrawPlayhead()));
    public IEnumerable? Tracks { get => (IEnumerable?)GetValue(TracksProperty); set => SetValue(TracksProperty, value); }
    public TrackViewModel? Selected { get => (TrackViewModel?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    /// <summary>Playhead in seconds from the session start.</summary>
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    private const double Ruler = 18, LaneMax = 24, Pad = 6, SnapPixels = 6, MaxStart = 3600;
    private static readonly double[] Steps = [.1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
    private readonly DrawingVisual playhead = new();
    private double span = 10;
    private TrackViewModel? dragging;
    private double grab, preview;
    private bool scrubbing;

    public TimelineView()
    {
        AddVisualChild(playhead);
        ClipToBounds = true; Focusable = true;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? playhead : throw new ArgumentOutOfRangeException(nameof(index));

    private List<TrackViewModel> Items => Tracks?.OfType<TrackViewModel>().ToList() ?? [];
    /// <summary>End of the last clip in seconds.</summary>
    public static double End(IEnumerable<TrackViewModel> tracks) => tracks.Select(t => t.StartSeconds + t.Audio.Duration).DefaultIfEmpty(0).Max();
    private double Width0 => Math.Max(1, ActualWidth - 2 * Pad);
    private double X(double seconds) => Pad + seconds / span * Width0;
    private double Seconds(double x) => Math.Max(0, (x - Pad) / Width0 * span);
    private double Lane(int count) => count == 0 ? LaneMax : Math.Min(LaneMax, (ActualHeight - Ruler) / count);
    private static double Start(TrackViewModel track, TrackViewModel? dragged, double moved) => ReferenceEquals(track, dragged) ? moved : track.StartSeconds;

    private void Subscribe(IEnumerable? old, IEnumerable? now)
    {
        if (old is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= CollectionChanged;
        if (old is not null) foreach (var t in old.OfType<TrackViewModel>()) t.PropertyChanged -= TrackChanged;
        if (now is INotifyCollectionChanged collection) collection.CollectionChanged += CollectionChanged;
        if (now is not null) foreach (var t in now.OfType<TrackViewModel>()) t.PropertyChanged += TrackChanged;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (var t in e.OldItems.OfType<TrackViewModel>()) t.PropertyChanged -= TrackChanged;
        if (e.NewItems is not null) foreach (var t in e.NewItems.OfType<TrackViewModel>()) t.PropertyChanged += TrackChanged;
        // Clear() raises Reset without OldItems; resubscribe from the current list.
        if (e.Action == NotifyCollectionChangedAction.Reset && Tracks is not null)
            foreach (var t in Tracks.OfType<TrackViewModel>()) { t.PropertyChanged -= TrackChanged; t.PropertyChanged += TrackChanged; }
        if (dragging is not null && !Items.Contains(dragging)) CancelDrag();
        InvalidateVisual();
    }
    private void TrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(TrackViewModel.StartSeconds) or nameof(TrackViewModel.Name) or nameof(TrackViewModel.Mute) or nameof(TrackViewModel.Solo))
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var items = Items;
        // The scale stays fixed while a clip is dragged so the clip does not slide under the pointer.
        if (dragging is null) span = Math.Max(10, End(items) * 1.06);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = new SolidColorBrush(Color.FromRgb(148, 162, 180));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(36, 44, 54)), 1);
        var tick = new Pen(new SolidColorBrush(Color.FromRgb(80, 94, 110)), 1);
        FormattedText Text(string s, Brush brush, double size = 9) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 20, 26)), null, new(0, 0, ActualWidth, ActualHeight));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 27, 34)), null, new(0, 0, ActualWidth, Ruler));
        double step = Steps.FirstOrDefault(s => s / span * Width0 >= 64, 1200);
        for (int i = 0; i * step <= span; i++)
        {
            double t = i * step, x = X(t);
            dc.DrawLine(grid, new(x, Ruler), new(x, ActualHeight));
            dc.DrawLine(tick, new(x, Ruler - 6), new(x, Ruler));
            dc.DrawText(Text(Label(t, step), muted), new(x + 3, 2));
        }
        if (items.Count == 0)
        {
            var hint = Text("Import WAV files · each track becomes a clip here · click the ruler to place the playhead", muted, 11);
            dc.DrawText(hint, new(Math.Max(Pad, (ActualWidth - hint.Width) / 2), Ruler + Math.Max(4, (ActualHeight - Ruler - hint.Height) / 2)));
            DrawPlayhead();
            return;
        }

        double lane = Lane(items.Count);
        bool solo = items.Any(t => t.Solo);
        var accent = Color.FromRgb(221, 184, 121);
        for (int i = 0; i < items.Count; i++)
        {
            var track = items[i];
            bool audible = !track.Mute && (!solo || track.Solo), selected = ReferenceEquals(track, Selected);
            double top = Ruler + i * lane, start = Start(track, dragging, preview);
            double x0 = X(start), x1 = Math.Max(x0 + 2, X(start + track.Audio.Duration));
            var box = new Rect(x0, top + 1, x1 - x0, Math.Max(1, lane - 2));
            dc.DrawRectangle(new SolidColorBrush(audible ? Color.FromRgb(40, 54, 64) : Color.FromRgb(32, 37, 44)),
                new Pen(new SolidColorBrush(selected ? accent : Color.FromRgb(64, 82, 96)), selected ? 1.5 : 1), box);
            DrawPeaks(dc, track, box, audible ? Color.FromRgb(115, 182, 200) : Color.FromRgb(84, 96, 108));
            if (lane >= 13)
            {
                var name = Text(track.Name + (track.Mute ? " · muted" : ""), new SolidColorBrush(selected ? accent : Color.FromRgb(214, 222, 230)), 10);
                name.MaxTextWidth = Math.Max(1, box.Width - 8); name.MaxLineCount = 1; name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new(x0 + 4, top + (lane - name.Height) / 2));
            }
        }
        if (dragging is not null)
        {
            var at = Text($"start {Clock(preview)}", new SolidColorBrush(accent), 10);
            dc.DrawText(at, new(Math.Clamp(X(preview), Pad, Math.Max(Pad, ActualWidth - at.Width - Pad)), Ruler + 2));
        }
        DrawPlayhead();
    }

    // Peak envelope of the clip's overview in dB, one column per pixel inside the visible part of the clip.
    private void DrawPeaks(DrawingContext dc, TrackViewModel track, Rect box, Color color)
    {
        var overview = track.Audio.Overview;
        if (overview.Length == 0 || box.Height < 4) return;
        double first = Math.Max(box.Left, 0), last = Math.Min(box.Right, ActualWidth), mid = box.Top + box.Height / 2, half = box.Height * .42;
        if (last - first < 1) return;
        int columns = (int)(last - first);
        var top = new Point[columns];
        for (int c = 0; c < columns; c++)
        {
            double x = first + c;
            long b0 = (long)((x - box.Left) / box.Width * overview.Length), b1 = Math.Max(b0 + 1, (long)((x + 1 - box.Left) / box.Width * overview.Length));
            float peak = 0;
            for (long b = Math.Max(0, b0); b < b1 && b < overview.Length; b++) peak = Math.Max(peak, overview[b]);
            // A 48 dB scale keeps quiet dialogue and beds readable in a short lane.
            double level = peak > 0 ? Math.Clamp((20 * Math.Log10(peak) + 48) / 48, 0, 1) : 0;
            top[c] = new(x, mid - level * half);
        }
        var envelope = new StreamGeometry();
        using (var context = envelope.Open())
        {
            context.BeginFigure(top[0], true, true);
            for (int c = 1; c < columns; c++) context.LineTo(top[c], false, false);
            for (int c = columns - 1; c >= 0; c--) context.LineTo(new(top[c].X, 2 * mid - top[c].Y), false, false);
        }
        envelope.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B)), null, envelope);
    }

    private void DrawPlayhead()
    {
        using var dc = playhead.RenderOpen();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double x = X(Math.Clamp(Playhead, 0, span));
        var accent = new SolidColorBrush(Color.FromRgb(240, 200, 120));
        dc.DrawLine(new Pen(accent, 1.5), new(x, 0), new(x, ActualHeight));
        var marker = new StreamGeometry();
        using (var context = marker.Open())
        {
            context.BeginFigure(new(x - 5, 0), true, true);
            context.LineTo(new(x + 5, 0), false, false); context.LineTo(new(x, 7), false, false);
        }
        marker.Freeze();
        dc.DrawGeometry(accent, null, marker);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); DrawPlayhead(); }

    public static string Clock(double seconds)
    {
        seconds = Math.Max(0, seconds);
        int minutes = (int)(seconds / 60);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds - minutes * 60:00.00}");
    }
    private static string Label(double seconds, double step)
    {
        int minutes = (int)(seconds / 60 + 1e-9);
        double rest = seconds - minutes * 60;
        return step < 1 ? string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00.0}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{Math.Round(rest):00}");
    }

    private void SetPlayhead(double seconds) => SetCurrentValue(PlayheadProperty, Math.Round(Math.Clamp(seconds, 0, End(Items)), 3));

    // Snaps to zero, the playhead and the edges of other clips within a few pixels.
    private double Snap(double start, TrackViewModel track)
    {
        double duration = track.Audio.Duration, best = start, distance = SnapPixels / Width0 * span;
        var targets = new List<double> { 0, Playhead, Playhead - duration };
        foreach (var other in Items.Where(t => !ReferenceEquals(t, track)))
            targets.AddRange([other.StartSeconds, other.StartSeconds + other.Audio.Duration, other.StartSeconds - duration, other.StartSeconds + other.Audio.Duration - duration]);
        foreach (var target in targets)
            if (Math.Abs(target - start) < distance) { distance = Math.Abs(target - start); best = target; }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        Focus(); e.Handled = true;
        var point = e.GetPosition(this);
        var items = Items;
        double lane = Lane(items.Count);
        int index = point.Y < Ruler ? -1 : (int)((point.Y - Ruler) / lane);
        if (index >= 0 && index < items.Count)
        {
            var track = items[index];
            SetCurrentValue(SelectedProperty, track);
            if (point.X >= X(track.StartSeconds) - 3 && point.X <= X(track.StartSeconds + track.Audio.Duration) + 3)
            {
                dragging = track; preview = track.StartSeconds; grab = Seconds(point.X) - track.StartSeconds;
                CaptureMouse(); Cursor = Cursors.SizeWE;
                return;
            }
        }
        scrubbing = true; CaptureMouse();
        SetPlayhead(Seconds(point.X));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var point = e.GetPosition(this);
        if (scrubbing) { SetPlayhead(Seconds(point.X)); return; }
        if (dragging is null) return;
        double start = (point.X - Pad) / Width0 * span - grab;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) start = Snap(start, dragging);
        start = Math.Round(Math.Clamp(start, 0, MaxStart), 3);
        if (start != preview) { preview = start; InvalidateVisual(); }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var track = dragging;
        double start = preview;
        dragging = null; scrubbing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ClearValue(CursorProperty);
        // One edit per drag: playback restarts once and undo gets a single step.
        if (track is not null && start != track.StartSeconds && Items.Contains(track)) track.StartSeconds = start;
        InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging is not null) CancelDrag();
        scrubbing = false;
    }
    private void CancelDrag() { dragging = null; ClearValue(CursorProperty); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsEnabled || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        double step = Keyboard.Modifiers == ModifierKeys.Shift ? .1 : 1;
        double? target = e.Key switch { Key.Left => Playhead - step, Key.Right => Playhead + step, Key.Home => 0, Key.End => End(Items), _ => null };
        if (target is { } seconds) { SetPlayhead(seconds); e.Handled = true; }
    }
}
