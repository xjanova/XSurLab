using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XSurLab.Core;

namespace XSurLab.App;

public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty AudioProperty = DependencyProperty.Register(nameof(Audio), typeof(AudioBuffer), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public AudioBuffer? Audio { get => (AudioBuffer?)GetValue(AudioProperty); set => SetValue(AudioProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var line = new Pen(new SolidColorBrush(Color.FromRgb(48, 57, 68)), 1);
        dc.DrawLine(line, new(0, ActualHeight / 2), new(ActualWidth, ActualHeight / 2));
        if (Audio is not { } audio || ActualWidth <= 0 || ActualHeight <= 0) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(115, 182, 200)), 1);
        int columns = Math.Max(1, (int)ActualWidth);
        var overview = audio.Overview;
        for (int x = 0; x < columns; x++)
        {
            long start = (long)x * overview.Length / columns, end = Math.Max(start + 1, (long)(x + 1) * overview.Length / columns);
            float peak = 0;
            for (long block = start; block < end && block < overview.Length; block++) peak = Math.Max(peak, overview[block]);
            double half = Math.Clamp(peak, 0, 1) * ActualHeight * .46;
            dc.DrawLine(pen, new(x, ActualHeight / 2 - half), new(x, ActualHeight / 2 + half));
        }
    }
}

/// <summary>
/// Top-down panner. Ear-level speakers sit on the outer ring; height speakers and elevated sources are drawn where they
/// project onto the floor (radius × cos elevation), so the centre is straight overhead. The arc shows the spread.
/// </summary>
public sealed class PannerView : FrameworkElement
{
    public static readonly DependencyProperty AzimuthProperty = DependencyProperty.Register(nameof(Azimuth), typeof(double), typeof(PannerView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.Register(nameof(Elevation), typeof(double), typeof(PannerView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SpreadProperty = DependencyProperty.Register(nameof(Spread), typeof(double), typeof(PannerView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(nameof(Layout), typeof(SpeakerLayout), typeof(PannerView),
        new FrameworkPropertyMetadata(SpeakerLayout.Surround51, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Azimuth { get => (double)GetValue(AzimuthProperty); set => SetValue(AzimuthProperty, value); }
    public double Elevation { get => (double)GetValue(ElevationProperty); set => SetValue(ElevationProperty, value); }
    public double Spread { get => (double)GetValue(SpreadProperty); set => SetValue(SpreadProperty, value); }
    public SpeakerLayout Layout { get => (SpeakerLayout)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    private const double SourceRing = .8;
    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    private double Radius => Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 24);
    private static double Floor(double elevation) => Math.Cos(Math.Clamp(elevation, 0, 90) * Math.PI / 180);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        var stroke = new Pen(new SolidColorBrush(Color.FromRgb(60, 73, 86)), 1);
        var accent = new SolidColorBrush(Color.FromRgb(221, 184, 121));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawEllipse(null, stroke, Center, Radius, Radius);
        dc.DrawEllipse(null, stroke, Center, Radius * .5, Radius * .5);
        dc.DrawLine(stroke, new(Center.X - Radius, Center.Y), new(Center.X + Radius, Center.Y));
        dc.DrawLine(stroke, new(Center.X, Center.Y - Radius), new(Center.X, Center.Y + Radius));
        var heights = SpatialMixer.HasHeights(Layout);
        // Markers shrink with the panner so a small window keeps 7.1.4 readable.
        double size = Math.Clamp(Radius * .13, 8, 15), halo = Math.Clamp(Radius * .16, 10, 18);
        if (heights)
        {
            var ring = new Pen(new SolidColorBrush(Color.FromRgb(70, 96, 110)), 1) { DashStyle = DashStyles.Dash };
            double r = Radius * SourceRing * Floor(45);
            dc.DrawEllipse(null, ring, Center, r, r);
        }
        foreach (var speaker in SpatialMixer.Speakers(Layout).Where(s => !s.IsLfe))
        {
            bool top = speaker.Elevation > 0;
            var p = Position(speaker.Azimuth, top ? SourceRing * Floor(speaker.Elevation) : 1);
            dc.DrawEllipse(new SolidColorBrush(top ? Color.FromRgb(30, 46, 56) : Color.FromRgb(36, 43, 52)), stroke, p, top ? size * .85 : size, top ? size * .85 : size);
            var text = new FormattedText(speaker.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Clamp(size * (top ? .62 : .68), 7, 10), top ? Brushes.LightSkyBlue : Brushes.LightGray, dpi);
            dc.DrawText(text, new(p.X - text.Width / 2, p.Y - text.Height / 2));
        }
        dc.DrawEllipse(Brushes.LightSlateGray, null, Center, 4, 4);
        if (!IsEnabled) return;
        // Without height speakers the elevation is kept but not heard, so the source stays drawn at ear level.
        double scale = SourceRing * (heights ? Floor(Elevation) : 1);
        var source = Position(Azimuth, scale);
        var line = new Pen(accent, 1);
        if (Spread > 0) DrawSpread(dc, new Pen(new SolidColorBrush(Color.FromArgb(150, 221, 184, 121)), Math.Clamp(Radius * .05, 3, 6)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, scale);
        dc.DrawLine(line, Center, source);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(32, 221, 184, 121)), line, source, halo, halo);
        dc.DrawEllipse(accent, null, source, halo * .4, halo * .4);
    }

    private void DrawSpread(DrawingContext dc, Pen pen, double scale)
    {
        double radius = Radius * Math.Max(scale, .12), width = Math.Min(Spread, 360);
        if (width >= 359.5) { dc.DrawEllipse(null, pen, Center, radius, radius); return; }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Position(Azimuth - width / 2, radius / Radius), false, false);
            context.ArcTo(Position(Azimuth + width / 2, radius / Radius), new(radius, radius), 0, width > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private Point Position(double degrees, double scale)
    {
        var radians = degrees * Math.PI / 180;
        return new(Center.X + Math.Sin(radians) * Radius * scale, Center.Y - Math.Cos(radians) * Radius * scale);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        CaptureMouse(); Move(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Move(e.GetPosition(this));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
    public PannerView() { IsEnabledChanged += (_, _) => InvalidateVisual(); }
    private void Move(Point point)
    {
        double dx = point.X - Center.X, dy = Center.Y - point.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        // Right at the centre the angle is noise, so only elevation changes there.
        if (distance > 3) SetCurrentValue(AzimuthProperty, Math.Round(Math.Atan2(dx, dy) * 180 / Math.PI));
        if (!SpatialMixer.HasHeights(Layout) || Radius <= 0) return;
        SetCurrentValue(ElevationProperty, Math.Round(Math.Acos(Math.Min(1, distance / (Radius * SourceRing))) * 180 / Math.PI));
    }
}


/// <summary>Vertical sample-peak bars per output channel with 1.5 s peak hold; bars fall at 12 dB/s.</summary>
public sealed class MeterView : FrameworkElement
{
    private const double Floor = -60, Fall = 12, Hold = 1.5, Scale = 26;
    private static readonly int[] Ticks = [0, -6, -12, -18, -24, -36, -48, -60];
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private string[] labels = [];
    private double[] bars = [], holds = [], ages = [];
    private double last;

    public void SetChannels(IReadOnlyList<string> names)
    {
        if (labels.SequenceEqual(names)) return;
        labels = names.ToArray();
        bars = Enumerable.Repeat(Floor, labels.Length).ToArray();
        holds = (double[])bars.Clone(); ages = new double[labels.Length];
        InvalidateVisual();
    }

    /// <summary>Feeds linear peaks of a new reading, or null when no new audio arrived since the last call.</summary>
    public void Show(IReadOnlyList<double>? peaks)
    {
        double now = clock.Elapsed.TotalSeconds, elapsed = Math.Clamp(now - last, 0, .5);
        last = now;
        bool moving = false;
        for (int c = 0; c < bars.Length; c++)
        {
            double level = peaks is not null && c < peaks.Count && peaks[c] > 0 ? Math.Max(Floor, 20 * Math.Log10(peaks[c])) : Floor;
            double bar = Math.Max(level, bars[c] - Fall * elapsed);
            if (level >= holds[c]) { holds[c] = level; ages[c] = 0; }
            else if ((ages[c] += elapsed) > Hold) holds[c] = Math.Max(Floor, holds[c] - 20 * elapsed);
            moving |= bar != bars[c] || holds[c] > Floor;
            bars[c] = bar;
        }
        if (moving || peaks is not null) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        if (labels.Length == 0 || ActualWidth <= Scale + 10) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = new SolidColorBrush(Color.FromRgb(148, 162, 180));
        double column = (ActualWidth - Scale) / labels.Length, width = Math.Max(3, column - 4);
        var names = labels.Select(l => new FormattedText(l, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, muted, dpi)).ToArray();
        // Narrow columns (10 or 12 channels) alternate their names over two rows so they do not overlap.
        bool stagger = names.Any(n => n.Width > column - 1);
        double top = 6, bottom = ActualHeight - (stagger ? 30 : 18), height = bottom - top;
        if (height <= 20) return;
        var line = new Pen(new SolidColorBrush(Color.FromRgb(48, 57, 68)), 1);
        double Y(double db) => top + (Math.Min(0, db) / Floor) * height;
        foreach (int tick in Ticks)
        {
            dc.DrawLine(line, new(Scale - 3, Y(tick)), new(ActualWidth, Y(tick)));
            var text = new FormattedText(tick.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 9, muted, dpi);
            dc.DrawText(text, new(Scale - 5 - text.Width, Y(tick) - text.Height / 2));
        }
        var teal = new SolidColorBrush(Color.FromRgb(115, 182, 200));
        var amber = new SolidColorBrush(Color.FromRgb(221, 184, 121));
        var red = new SolidColorBrush(Color.FromRgb(224, 108, 90));
        var track = new SolidColorBrush(Color.FromRgb(16, 20, 26));
        for (int c = 0; c < labels.Length; c++)
        {
            double x = Scale + c * column + (column - width) / 2;
            dc.DrawRectangle(track, null, new(x, top, width, height));
            if (bars[c] > Floor)
            {
                var fill = bars[c] > -1 ? red : bars[c] > -9 ? amber : teal;
                dc.DrawRectangle(fill, null, new(x, Y(bars[c]), width, bottom - Y(bars[c])));
            }
            if (holds[c] > Floor) dc.DrawRectangle(holds[c] > -1 ? red : Brushes.WhiteSmoke, null, new(x, Y(holds[c]) - 1, width, 2));
            dc.DrawText(names[c], new(x + width / 2 - names[c].Width / 2, bottom + 3 + (stagger && c % 2 == 1 ? 12 : 0)));
        }
    }
}

public sealed class DisplayValueConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SpeakerLayout.Stereo => "Stereo 2.0",
        SpeakerLayout.Surround51 => "5.1 Surround",
        SpeakerLayout.Surround71 => "7.1 Surround",
        SpeakerLayout.Surround51Back => "5.1 Back",
        SpeakerLayout.Surround514 => "5.1.4",
        SpeakerLayout.Surround714 => "7.1.4",
        LayoutOption option => option.Name,
        FormatOption option => option.Name,
        XSurLab.Audio.AudioDevice device => device.DisplayName,
        int rate => $"{rate / 1000d:0.#} kHz",
        _ => value.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
