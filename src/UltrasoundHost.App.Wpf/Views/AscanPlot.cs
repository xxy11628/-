using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UltrasoundHost.Application;

namespace UltrasoundHost.App.Wpf.Views;

/// <summary>
/// Draws one A-scan waveform (a single receive channel) with the gate window and threshold shaded.
/// The snapshot it receives only contains the channels the views asked for (05 §7), and may be
/// peak-preservingly decimated, so it is drawn from the snapshot's own sample times.
/// </summary>
public sealed class AscanPlot : FrameworkElement
{
    private const double LeftMargin = 46;
    private const double RightMargin = 14;
    private const double TopMargin = 14;
    private const double BottomMargin = 26;

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x19)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x8A, 0xC8, 0xE8)));
    private static readonly Brush GateBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xC0, 0x40)));
    private static readonly Brush GateEdgeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xC0, 0x40)));
    private static readonly Brush TraceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xE6, 0x9B)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xB2)));
    private static readonly Brush HintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x80)));
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Pen TracePen = Freeze(new Pen(TraceBrush, 1.2));
    private static readonly Pen GateEdgePen = Freeze(new Pen(GateEdgeBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private FrameSnapshot? _frame;
    private int _channel;

    public AscanPlot()
    {
        MinHeight = 260;
        SnapsToDevicePixels = true;
    }

    /// <summary>Publishes a new frame. Call on the UI thread; the plot repaints itself.</summary>
    public void Update(FrameSnapshot? frame, int channel)
    {
        _frame = frame;
        _channel = channel;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        double plotWidth = width - LeftMargin - RightMargin;
        double plotHeight = height - TopMargin - BottomMargin;
        if (plotWidth <= 4 || plotHeight <= 4)
        {
            return;
        }

        FrameSnapshot? frame = _frame;
        if (frame is null || frame.ChannelCount == 0)
        {
            DrawHint(drawingContext, "暂无数据：请打开模拟板卡并点击“开始采集”。", width, height);
            return;
        }

        int channelIndex = frame.IndexOfChannel(_channel);
        if (channelIndex < 0)
        {
            channelIndex = 0;
        }

        short[] samples = frame.Channels[channelIndex];
        if (samples.Length < 2)
        {
            DrawHint(drawingContext, "该帧没有采样点。", width, height);
            return;
        }

        int channelNumber = frame.ChannelNumbers[channelIndex];
        double duration = frame.DurationSeconds > 0 ? frame.DurationSeconds : 1.0;
        double start = frame.StartDelaySeconds;
        double middle = TopMargin + (plotHeight / 2.0);

        // Horizontal amplitude grid: -1, -0.5, 0, +0.5, +1 of full scale.
        for (int step = -2; step <= 2; step++)
        {
            double y = middle - (step / 2.0 * plotHeight / 2.0);
            drawingContext.DrawLine(GridPen, new Point(LeftMargin, y), new Point(LeftMargin + plotWidth, y));
            DrawLabel(drawingContext, (step / 2.0).ToString("0.0", CultureInfo.InvariantCulture), 6, y - 7);
        }

        // Vertical time grid: ten divisions of the recorded window.
        for (int step = 0; step <= 10; step++)
        {
            double x = LeftMargin + (step / 10.0 * plotWidth);
            drawingContext.DrawLine(GridPen, new Point(x, TopMargin), new Point(x, TopMargin + plotHeight));
            if (step % 2 == 0)
            {
                double microseconds = (start + (step / 10.0 * duration)) * 1e6;
                DrawLabel(drawingContext, microseconds.ToString("0.0", CultureInfo.InvariantCulture), x - 8, TopMargin + plotHeight + 4);
            }
        }

        double TimeToX(double seconds) => LeftMargin + ((seconds - start) / duration * plotWidth);

        // Gate window and threshold, both in the same time axis as the samples.
        double gateStartX = TimeToX(frame.Gate.StartSeconds);
        double gateEndX = TimeToX(frame.Gate.EndSeconds);
        double threshold = Math.Clamp(frame.Gate.Threshold, 0, 1) * (plotHeight / 2.0);
        double zeroY = middle;
        double gateLeft = Math.Clamp(Math.Min(gateStartX, gateEndX), LeftMargin, LeftMargin + plotWidth);
        double gateRight = Math.Clamp(Math.Max(gateStartX, gateEndX), LeftMargin, LeftMargin + plotWidth);
        if (gateRight > gateLeft)
        {
            drawingContext.DrawRectangle(GateBrush, null, new Rect(gateLeft, TopMargin, gateRight - gateLeft, plotHeight));
        }

        drawingContext.DrawLine(GateEdgePen, new Point(LeftMargin, zeroY - threshold), new Point(LeftMargin + plotWidth, zeroY - threshold));
        drawingContext.DrawLine(GateEdgePen, new Point(LeftMargin, zeroY + threshold), new Point(LeftMargin + plotWidth, zeroY + threshold));

        // The waveform itself. Decimated snapshots contain min/max pairs, so the polyline still
        // reaches every extreme of the original samples.
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            double scaleY = (plotHeight / 2.0) / Math.Max(1.0, frame.Adc.FullScaleCounts);
            for (int i = 0; i < samples.Length; i++)
            {
                double time = frame.SampleTimeSeconds(channelIndex, i);
                double x = TimeToX(time);
                double y = zeroY - (samples[i] * scaleY);
                if (i == 0)
                {
                    context.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, TracePen, geometry);

        DrawLabel(drawingContext, "通道 " + channelNumber + "   帧 #" + frame.Sequence + "   参数版本 v" + frame.ParamVersion, LeftMargin, 2);
        DrawLabel(drawingContext, "µs", LeftMargin + plotWidth - 10, TopMargin + plotHeight + 4);
    }

    private void DrawHint(DrawingContext drawingContext, string text, double width, double height)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            13,
            HintBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(formatted, new Point((width - formatted.Width) / 2, height / 2 - formatted.Height));
    }

    private void DrawLabel(DrawingContext drawingContext, string text, double x, double y)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            10.5,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(formatted, new Point(x, y));
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
