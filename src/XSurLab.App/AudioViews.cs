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
        var samples = audio.Samples.Span;
        for (int x = 0; x < columns; x++)
        {
            int start = (int)((long)x * audio.Frames / columns), end = (int)((long)(x + 1) * audio.Frames / columns);
            float peak = 0;
            for (int frame = start; frame < end; frame++)
                for (int c = 0; c < audio.Channels; c++) peak = Math.Max(peak, Math.Abs(samples[frame * audio.Channels + c]));
            double half = Math.Clamp(peak, 0, 1) * ActualHeight * .46;
            dc.DrawLine(pen, new(x, ActualHeight / 2 - half), new(x, ActualHeight / 2 + half));
        }
    }
}

public sealed class PannerView : FrameworkElement
{
    public static readonly DependencyProperty AzimuthProperty = DependencyProperty.Register(nameof(Azimuth), typeof(double), typeof(PannerView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(nameof(Layout), typeof(SpeakerLayout), typeof(PannerView),
        new FrameworkPropertyMetadata(SpeakerLayout.Surround51, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Azimuth { get => (double)GetValue(AzimuthProperty); set => SetValue(AzimuthProperty, value); }
    public SpeakerLayout Layout { get => (SpeakerLayout)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    private double Radius => Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 24);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        var stroke = new Pen(new SolidColorBrush(Color.FromRgb(60, 73, 86)), 1);
        var accent = new SolidColorBrush(Color.FromRgb(221, 184, 121));
        dc.DrawEllipse(null, stroke, Center, Radius, Radius);
        dc.DrawEllipse(null, stroke, Center, Radius * .5, Radius * .5);
        dc.DrawLine(stroke, new(Center.X - Radius, Center.Y), new(Center.X + Radius, Center.Y));
        dc.DrawLine(stroke, new(Center.X, Center.Y - Radius), new(Center.X, Center.Y + Radius));
        foreach (var speaker in SpatialMixer.Speakers(Layout).Where(s => !s.IsLfe))
        {
            var p = Position(speaker.Azimuth, 1);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(36, 43, 52)), stroke, p, 15, 15);
            var text = new FormattedText(speaker.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new(p.X - text.Width / 2, p.Y - text.Height / 2));
        }
        dc.DrawEllipse(Brushes.LightSlateGray, null, Center, 4, 4);
        if (!IsEnabled) return;
        var source = Position(Azimuth, .68);
        dc.DrawLine(new Pen(accent, 1), Center, source);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(32, 221, 184, 121)), new Pen(accent, 1), source, 18, 18);
        dc.DrawEllipse(accent, null, source, 7, 7);
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
    private void Move(Point point) => SetCurrentValue(AzimuthProperty, Math.Round(Math.Atan2(point.X - Center.X, Center.Y - point.Y) * 180 / Math.PI));
}


public sealed class DisplayValueConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SpeakerLayout.Stereo => "Stereo 2.0",
        SpeakerLayout.Surround51 => "5.1 Surround",
        SpeakerLayout.Surround71 => "7.1 Surround",
        SpeakerLayout.Surround51Back => "5.1 Back",
        LayoutOption option => option.Name,
        XSurLab.Audio.AudioDevice device => device.DisplayName,
        int rate => $"{rate / 1000d:0.#} kHz",
        _ => value.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
