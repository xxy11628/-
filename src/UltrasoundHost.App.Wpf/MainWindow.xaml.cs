using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UltrasoundHost.Application;
using UltrasoundHost.App.Wpf.Views;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;

namespace UltrasoundHost.App.Wpf;

/// <summary>
/// The WPF shell. It owns rendering and operator interaction only: the plugin host, device and
/// acquisition pump live in <see cref="ShellSession"/>. View tabs are built from the descriptors
/// active plugins contribute; kinds the shell does not know are shown as a placeholder.
/// All operator-facing text is Chinese; ids, units and view kinds stay as technical identifiers.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellSession _session;
    private readonly ObservableCollection<PluginRow> _pluginRows = new();
    private readonly ObservableCollection<SessionRow> _sessionRows = new();
    private readonly DispatcherTimer _refresh;

    private AscanPlot? _ascan;
    private GateTableView? _gateTable;
    private FrameSnapshot? _shownSnapshot;
    private ProcessedFrame? _shownInspection;
    private int _channel;
    private int _openedChannels = 1;

    public MainWindow(ShellSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        InitializeComponent();
        PluginList.ItemsSource = _pluginRows;
        SessionList.ItemsSource = _sessionRows;
        BuildViewTabs();
        RefreshPluginRows();
        RefreshSessionRows();
        UpdateButtons();

        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnRefreshTick, Dispatcher);
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();

        StatusText.Text = "就绪。请先打开模拟板卡。";
    }

    private void BuildViewTabs()
    {
        ViewTabs.Items.Clear();
        foreach (ViewDescriptor view in _session.Views)
        {
            FrameworkElement content;
            switch (view.ViewKind)
            {
                case ViewKinds.AScan:
                    _ascan = new AscanPlot();
                    content = _ascan;
                    break;

                case ViewKinds.GateTable:
                    _gateTable = new GateTableView();
                    content = _gateTable;
                    break;

                default:
                    content = new TextBlock
                    {
                        Text = "当前外壳没有能显示视图类型“" + view.ViewKind + "”的渲染器。",
                        Margin = new Thickness(16),
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    break;
            }

            ViewTabs.Items.Add(new TabItem { Header = view.Title, Content = content, Tag = view });
        }

        if (ViewTabs.Items.Count == 0)
        {
            ViewTabs.Items.Add(new TabItem
            {
                Header = "视图",
                Content = new TextBlock
                {
                    Text = "没有已启动的插件提供视图。",
                    Margin = new Thickness(16),
                    Foreground = Brushes.Gray,
                },
            });
        }

        ApplySchemeFilter();
    }

    private void ApplySchemeFilter()
    {
        DeviceCapabilities? capabilities = _session.DeviceCapabilities;
        foreach (object item in ViewTabs.Items)
        {
            if (item is not TabItem tab || tab.Tag is not ViewDescriptor view)
            {
                continue;
            }

            bool matches = capabilities is null || view.Matches(capabilities.Schemes);
            tab.IsEnabled = matches;
            tab.ToolTip = matches ? null : "当前设备采集的数据无法用该视图显示。";
        }
    }

    private void RefreshPluginRows()
    {
        _pluginRows.Clear();
        foreach (PluginStatus status in _session.Statuses)
        {
            _pluginRows.Add(new PluginRow(
                status.DisplayName,
                status.Id,
                KindText(status.Kind),
                StateText(status.State),
                status.Reason ?? string.Empty));
        }
    }

    private void RefreshSessionRows()
    {
        _sessionRows.Clear();
        if (_session.SessionCheck is not { } check)
        {
            return;
        }

        foreach (SessionCheckItem item in check.Items)
        {
            string name = _session.Statuses.FirstOrDefault(status => status.Id == item.PluginId)?.DisplayName ?? item.PluginId;
            _sessionRows.Add(new SessionRow(
                name,
                item.Satisfied ? "通过" : "不满足",
                string.Join("；", item.Unmet)));
        }
    }

    private void PopulateChannelChooser()
    {
        ChannelBox.Items.Clear();
        int count = Math.Max(1, _openedChannels);
        for (int index = 0; index < count; index++)
        {
            ChannelBox.Items.Add(index);
        }

        ChannelBox.SelectedIndex = 0;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int channels = ParseInt(ChannelsBox.Text, "通道数");
            SessionCheckResult check = await _session.OpenDeviceAsync(channels);

            _openedChannels = channels;
            PopulateChannelChooser();
            _session.SelectDisplayChannel(0);
            RefreshSessionRows();
            ApplySchemeFilter();
            ClearFrames();
            StatusText.Text = "设备 " + _session.DeviceId + "："
                + (check.AllSatisfied ? "会话检查通过。" : "会话检查未通过，“开始采集”已禁用。");
        }
        catch (Exception ex)
        {
            Report("无法打开设备", ex);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int gain = ParseInt(GainBox.Text, "增益");
            AcquisitionParameters? current = _session.Parameters;
            AcquisitionParameters parameters = current is null
                ? new AcquisitionParameters { Version = 1, GainDb = gain, ActiveReceiveChannels = _openedChannels }
                : current.Revise(previous => previous with { GainDb = gain, ActiveReceiveChannels = _openedChannels });

            await _session.StartAcquisitionAsync(parameters);
            StatusText.Text = "正在采集，参数版本 " + parameters.Version + "。";
        }
        catch (Exception ex)
        {
            Report("无法开始采集", ex);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.StopAcquisitionAsync();
            StatusText.Text = "采集已停止。";
        }
        catch (Exception ex)
        {
            Report("无法停止采集", ex);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        AcquisitionParameters? current = _session.Parameters;
        if (current is null)
        {
            StatusText.Text = "请先点击“开始采集”，再修改参数。";
            return;
        }

        try
        {
            int gain = ParseInt(GainBox.Text, "增益");
            AcquisitionParameters revised = current.Revise(previous => previous with
            {
                GainDb = gain,
                ActiveReceiveChannels = _openedChannels,
            });
            await _session.ApplyParametersAsync(revised);
            StatusText.Text = "参数已应用，当前参数版本为 " + revised.Version
                + "。仍在途的旧版本帧将被丢弃。";
        }
        catch (Exception ex)
        {
            Report("无法应用参数", ex);
        }
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelBox.SelectedItem is int channel)
        {
            _channel = channel;
            _session.SelectDisplayChannel(channel);
            _ascan?.Update(_shownSnapshot, _channel);
        }
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        FrameSnapshot? snapshot = _session.LatestSnapshot;
        if (!ReferenceEquals(snapshot, _shownSnapshot))
        {
            _shownSnapshot = snapshot;
            _ascan?.Update(snapshot, _channel);
        }

        ProcessedFrame? inspection = _session.LatestInspection;
        if (!ReferenceEquals(inspection, _shownInspection))
        {
            _shownInspection = inspection;
            _gateTable?.Update(inspection, _session.Parameters?.Gate ?? GateConfig.Default);
        }

        UpdateButtons();
        UpdateFrameText();
    }

    private void UpdateButtons()
    {
        bool acquiring = _session.IsAcquiring;
        bool deviceOpen = _session.DeviceId is not null;
        OpenButton.IsEnabled = !acquiring;
        StartButton.IsEnabled = deviceOpen && !acquiring && _session.SessionCheck is { AllSatisfied: true };
        StopButton.IsEnabled = acquiring;
        ApplyButton.IsEnabled = deviceOpen;
    }

    private void UpdateFrameText()
    {
        LossCounters counters = _session.Counters;
        FrameSnapshot? snapshot = _session.LatestSnapshot;
        string frames = "帧 " + counters.Received
            + " | 判伤 " + counters.Inspection.Processed
            + " | 欠帧 " + (counters.Inspection.Dropped + counters.Inspection.UnknownVersion)
            + " | 断档 " + counters.SequenceGaps
            + " | 设备溢出 " + counters.DeviceOverflow
            + " | 显示丢帧 " + counters.Display.Dropped;

        if (snapshot is not null)
        {
            frames += " | 最新 #" + snapshot.Sequence + " v" + snapshot.ParamVersion
                + " | " + (snapshot.SampleRateHz / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MS/s"
                + " | " + snapshot.ChannelCount + " 通道";
        }

        if (counters.InspectionIncomplete)
        {
            frames += " | 检测不完整";
        }

        if (_session.LastError is { } error)
        {
            frames += " | 错误：" + error.Message;
        }

        FrameText.Text = frames;
    }

    private void ClearFrames()
    {
        _shownSnapshot = null;
        _shownInspection = null;
        _ascan?.Update(null, _channel);
        _gateTable?.Update(null, GateConfig.Default);
        FrameText.Text = string.Empty;
    }

    private void Report(string what, Exception ex)
    {
        StatusText.Text = what + "：" + ex.Message;
    }

    private static int ParseInt(string text, string name)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException("“" + name + "”必须是整数，实际为“" + text + "”");
        }

        return value;
    }

    private static string KindText(PluginKind kind) => kind switch
    {
        PluginKind.Device => "设备",
        PluginKind.Technique => "检测技术",
        PluginKind.Processing => "处理",
        PluginKind.View => "视图",
        PluginKind.Storage => "存储",
        _ => kind.ToString(),
    };

    private static string StateText(PluginState state) => state switch
    {
        PluginState.Discovered => "已发现",
        PluginState.Registered => "已注册",
        PluginState.Initialized => "已初始化",
        PluginState.Running => "运行中",
        PluginState.Stopped => "已停止",
        PluginState.Rejected => "已拒绝",
        PluginState.Faulted => "故障",
        _ => state.ToString(),
    };
}

public sealed record PluginRow(string Name, string Id, string Kind, string State, string Reason);

public sealed record SessionRow(string Plugin, string Verdict, string Details);
