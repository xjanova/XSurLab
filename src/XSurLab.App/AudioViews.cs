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


/// <summary>
/// Track EQ response on a log axis (20 Hz–20 kHz, ±24 dB) with a handle per band. Dragging a handle moves its band and
/// switches it on (gain bands also move vertically); the wheel sets Q; a double-click switches the band on or off.
/// </summary>
public sealed class EqView : FrameworkElement
{
    public static readonly DependencyProperty EqProperty = DependencyProperty.Register(nameof(Eq), typeof(TrackEq), typeof(EqView),
        new FrameworkPropertyMetadata(TrackEq.Flat, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SampleRateProperty = DependencyProperty.Register(nameof(SampleRate), typeof(int), typeof(EqView),
        new FrameworkPropertyMetadata(48000, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedBandProperty = DependencyProperty.Register(nameof(SelectedBand), typeof(int), typeof(EqView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public TrackEq? Eq { get => (TrackEq?)GetValue(EqProperty); set => SetValue(EqProperty, value); }
    public int SampleRate { get => (int)GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }
    public int SelectedBand { get => (int)GetValue(SelectedBandProperty); set => SetValue(SelectedBandProperty, value); }
    private const double MinHz = 20, Decades = 3, Range = 24;
    private bool dragging;
    private double Left => 30;
    private double Right => Math.Max(Left + 1, ActualWidth - 8);
    private double Top => 8;
    private double Bottom => Math.Max(Top + 1, ActualHeight - 18);
    private double X(double hz) => Left + (Math.Log10(hz / MinHz) / Decades) * (Right - Left);
    private double Frequency(double x) => MinHz * Math.Pow(10, Math.Clamp((x - Left) / (Right - Left), 0, 1) * Decades);
    private double Y(double db) => Top + (Range - Math.Clamp(db, -Range, Range)) / (2 * Range) * (Bottom - Top);
    private double Db(double y) => Range - (y - Top) / (Bottom - Top) * 2 * Range;

    /// <summary>Rounds a frequency to what the controls show: whole hertz below 1 kHz, 10 Hz steps above.</summary>
    public static double Nice(double hz) { hz = Math.Clamp(hz, 20, 20000); return hz < 1000 ? Math.Round(hz) : Math.Round(hz / 10) * 10; }
    public static string Hz(double hz) => hz < 1000 ? $"{hz:0} Hz" : $"{hz / 1000:0.##} kHz";

    public EqView() { IsEnabledChanged += (_, _) => InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 20, 26)), null, new(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 80 || ActualHeight < 60) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(40, 48, 58)), 1);
        var axis = new Pen(new SolidColorBrush(Color.FromRgb(70, 82, 96)), 1);
        var muted = new SolidColorBrush(Color.FromRgb(148, 162, 180));
        FormattedText Text(string s, Brush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, brush, dpi);
        foreach (var hz in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            dc.DrawLine(grid, new(X(hz), Top), new(X(hz), Bottom));
            if (hz is 100 or 1000 or 10000 or 50 or 500 or 5000) { var t = Text(hz < 1000 ? $"{hz}" : $"{hz / 1000}k", muted); dc.DrawText(t, new(X(hz) - t.Width / 2, Bottom + 3)); }
        }
        foreach (var db in new[] { -18, -12, -6, 0, 6, 12, 18 })
        {
            dc.DrawLine(db == 0 ? axis : grid, new(Left, Y(db)), new(Right, Y(db)));
            if (db % 12 == 0) { var t = Text(db > 0 ? $"+{db}" : db < 0 ? $"−{-db}" : "0", muted); dc.DrawText(t, new(Left - 5 - t.Width, Y(db) - t.Height / 2)); }
        }
        var eq = Eq ?? TrackEq.Flat;
        int rate = SampleRate > 0 ? SampleRate : 48000;
        var bands = eq.Design(rate);
        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            for (double x = Left; x <= Right; x += 1)
            {
                double hz = Frequency(x), db = bands.Sum(b => b.MagnitudeDb(Math.Min(hz, rate * .4999), rate));
                var point = new Point(x, Y(db));
                if (x == Left) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
            }
        }
        curve.Freeze();
        var accent = Color.FromRgb(221, 184, 121);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(IsEnabled ? accent : Color.FromRgb(90, 100, 112)), 2), curve);
        if (!IsEnabled) return;
        for (int i = 0; i < TrackEq.Count; i++)
        {
            var band = eq.Band(i);
            var p = Handle(eq, i);
            bool selected = i == SelectedBand;
            dc.DrawEllipse(band.On ? new SolidColorBrush(accent) : new SolidColorBrush(Color.FromRgb(36, 43, 52)),
                new Pen(selected ? Brushes.WhiteSmoke : new SolidColorBrush(band.On ? accent : Color.FromRgb(110, 122, 136)), selected ? 2 : 1), p, 6, 6);
            var label = Text((i + 1).ToString(CultureInfo.InvariantCulture), band.On ? new SolidColorBrush(Color.FromRgb(23, 28, 34)) : muted);
            dc.DrawText(label, new(p.X - label.Width / 2, p.Y - label.Height / 2));
            if (selected) { var name = Text(TrackEq.Names[i], Brushes.WhiteSmoke); dc.DrawText(name, new(Math.Clamp(p.X - name.Width / 2, Left, Right - name.Width), Math.Max(0, p.Y - 22))); }
        }
    }

    private Point Handle(TrackEq eq, int index)
    {
        var band = eq.Band(index);
        return new(X(band.Frequency), Y(TrackEq.HasGain(index) ? band.GainDb : 0));
    }
    private int Nearest(Point point)
    {
        var eq = Eq ?? TrackEq.Flat;
        int best = -1; double distance = 14;
        for (int i = 0; i < TrackEq.Count; i++)
        {
            var v = Handle(eq, i) - point;
            if (v.Length < distance) { distance = v.Length; best = i; }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        int band = Nearest(e.GetPosition(this));
        if (band < 0) return;
        SetCurrentValue(SelectedBandProperty, band);
        var eq = Eq ?? TrackEq.Flat;
        if (e.ClickCount == 2) { SetCurrentValue(EqProperty, eq.WithBand(band, eq.Band(band) with { On = !eq.Band(band).On })); e.Handled = true; return; }
        dragging = true; CaptureMouse(); Focus(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging || !IsMouseCaptured) return;
        var point = e.GetPosition(this);
        var eq = Eq ?? TrackEq.Flat;
        int index = SelectedBand;
        var band = eq.Band(index) with { On = true, Frequency = Nice(Frequency(point.X)) };
        if (TrackEq.HasGain(index)) band = band with { GainDb = Math.Clamp(Math.Round(Db(point.Y) * 2) / 2, -Range, Range) };
        if (band != eq.Band(index)) SetCurrentValue(EqProperty, eq.WithBand(index, band));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { dragging = false; ReleaseMouseCapture(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsEnabled || !TrackEq.HasQ(SelectedBand)) return;
        var eq = Eq ?? TrackEq.Flat;
        var band = eq.Band(SelectedBand);
        double q = Math.Clamp(Math.Round(band.Q * (e.Delta > 0 ? 1.12 : 1 / 1.12), 2), .1, 10);
        SetCurrentValue(EqProperty, eq.WithBand(SelectedBand, band with { On = true, Q = q }));
        e.Handled = true;
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
