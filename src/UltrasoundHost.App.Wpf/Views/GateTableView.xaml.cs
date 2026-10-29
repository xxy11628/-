using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.App.Wpf.Views;

/// <summary>One gate row, pre-formatted for the grid.</summary>
public sealed record GateRow(
    long Sequence,
    int ParamVersion,
    int Channel,
    string Triggered,
    string Amplitude,
    string DepthMm,
    string TimeUs);

/// <summary>Shows the latest inspection result (gates of the last processed frame).</summary>
public partial class GateTableView : UserControl
{
    private readonly ObservableCollection<GateRow> _rows = new();

    public GateTableView()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
    }

    public void Update(ProcessedFrame? inspection, GateConfig gate)
    {
        _rows.Clear();
        if (inspection is null)
        {
            Summary.Text = "暂无数据：请打开模拟板卡并点击“开始采集”。";
            return;
        }

        foreach (GateResult result in inspection.Gates)
        {
            _rows.Add(new GateRow(
                inspection.Sequence,
                inspection.ParamVersion,
                result.Channel,
                result.Triggered ? "触发" : "—",
                result.PeakAmplitude.ToString("F3", CultureInfo.InvariantCulture),
                (result.SoundPathMeters * 1000.0).ToString("F2", CultureInfo.InvariantCulture),
                (result.PeakTimeSeconds * 1e6).ToString("F2", CultureInfo.InvariantCulture)));
        }

        Summary.Text = "帧 #" + inspection.Sequence
            + "   参数版本 " + inspection.ParamVersion
            + "   闸门 " + (gate.StartSeconds * 1e6).ToString("F2", CultureInfo.InvariantCulture)
            + "–" + (gate.EndSeconds * 1e6).ToString("F2", CultureInfo.InvariantCulture) + " µs"
            + "   阈值 " + gate.Threshold.ToString("F2", CultureInfo.InvariantCulture);
    }
}
