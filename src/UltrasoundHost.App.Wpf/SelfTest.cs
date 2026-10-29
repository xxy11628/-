using System.Globalization;
using System.IO;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.App.Wpf;

/// <summary>
/// Headless end-to-end run of the shell's own code path, used by <c>--selftest</c> so the shell can
/// be verified without a desktop: plugin host, simulator, session check, pump, parameter revision,
/// snapshot and gate results. Writes a plain-text report and returns a process exit code.
/// </summary>
internal static class SelfTest
{
    public static int Run(string reportPath)
    {
        var lines = new List<string>();
        var session = new ShellSession(new IPlugin[]
        {
            new SimulatorPlugin(),
            new GatePeakPlugin(),
            new BasicViewPlugin(),
        });

        try
        {
            session.InitializeAsync().GetAwaiter().GetResult();
            lines.Add("plugins: " + string.Join(", ", session.Statuses.Select(status => status.Id + "=" + status.State)));
            if (session.Statuses.Any(status => status.State != PluginState.Running))
            {
                throw new InvalidOperationException("not every plugin is running");
            }

            if (session.Views.Count != 2)
            {
                throw new InvalidOperationException("expected two contributed views, got " + session.Views.Count);
            }

            lines.Add("views: " + string.Join(", ", session.Views.Select(view => view.Id + "/" + view.ViewKind + "/" + view.Title)));

            SessionCheckResult check = session.OpenDeviceAsync(2).GetAwaiter().GetResult();
            if (!check.AllSatisfied)
            {
                throw new InvalidOperationException(
                    "session check failed: " + string.Join("; ", check.Items.Where(item => !item.Satisfied).Select(item => item.PluginId)));
            }

            lines.Add("device: " + session.DeviceId + ", " + session.DeviceCapabilities?.ReceiveChannels + ", " + session.Probe?.Topology);
            lines.Add("session check: ok");

            var parameters = new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 2 };
            session.StartAcquisitionAsync(parameters).GetAwaiter().GetResult();

            WaitUntil(
                () => session.FramesProcessed >= 3 && session.LatestSnapshot is not null,
                TimeSpan.FromSeconds(10),
                "the first frames");
            ProcessedFrame first = session.LatestInspection ?? throw new InvalidOperationException("no inspection result");
            FrameSnapshot? display = session.LatestSnapshot;
            if (first.ParamVersion != 1 || first.Gates.Count != 2 || display is null || display.ChannelCount != 2)
            {
                throw new InvalidOperationException("unexpected first-frame shapes");
            }

            if (first.Gates.Any(gate => !gate.Triggered))
            {
                throw new InvalidOperationException("the simulated defect was not detected in v1");
            }

            lines.Add(FormatFrame("v1", first));

            long framesBefore = session.FramesProcessed;
            AcquisitionParameters revised = parameters.Revise(previous => previous with { GainDb = 6 });
            session.ApplyParametersAsync(revised).GetAwaiter().GetResult();

            WaitUntil(
                () => session.LatestInspection is { ParamVersion: 2 } && session.FramesProcessed > framesBefore + 1,
                TimeSpan.FromSeconds(10),
                "frames acquired with version 2");

            ProcessedFrame second = session.LatestInspection!;
            if (second.Gates.Count != 2)
            {
                throw new InvalidOperationException("unexpected inspection shape in v2");
            }

            lines.Add(FormatFrame("v2", second));
            if (!(second.Gates[0].PeakAmplitude > first.Gates[0].PeakAmplitude))
            {
                throw new InvalidOperationException("raising the gain did not raise the measured amplitude");
            }

            session.StopAcquisitionAsync().GetAwaiter().GetResult();

            LossCounters counters = session.Counters;
            foreach (FramePathKind path in new[] { FramePathKind.Inspection, FramePathKind.Display })
            {
                PathCounters pathCounters = counters[path];
                if (pathCounters.Offered != counters.Received)
                {
                    throw new InvalidOperationException(path + ": offered " + pathCounters.Offered + " != received " + counters.Received);
                }

                long disposed = pathCounters.Processed + pathCounters.Dropped + pathCounters.UnknownVersion;
                if (pathCounters.Offered != disposed)
                {
                    throw new InvalidOperationException(path + ": offered " + pathCounters.Offered + " != accounted " + disposed);
                }
            }

            if (counters.InspectionIncomplete)
            {
                throw new InvalidOperationException("the inspection path reports incomplete data");
            }

            lines.Add("counters: received=" + counters.Received
                + ", inspection processed=" + counters.Inspection.Processed
                + ", inspection dropped=" + counters.Inspection.Dropped
                + ", display processed=" + counters.Display.Processed
                + ", display dropped=" + counters.Display.Dropped
                + ", sequence gaps=" + counters.SequenceGaps);
            lines.Add("RESULT: OK");
            return 0;
        }
        catch (Exception ex)
        {
            lines.Add("RESULT: FAIL");
            lines.Add(ex.ToString());
            return 1;
        }
        finally
        {
            try
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                lines.Add("dispose failed: " + ex.Message);
            }

            WriteReport(reportPath, lines);
        }
    }

    private static string FormatFrame(string label, ProcessedFrame inspection)
    {
        if (inspection.Gates.Count == 0)
        {
            return label + ": frame #" + inspection.Sequence + " (no gate results)";
        }

        GateResult gate = inspection.Gates[0];
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: frame #{1}, gate ch{2} amp={3:F3} depth={4:F2} mm, path={5:F2} us",
            label,
            inspection.Sequence,
            gate.Channel,
            gate.PeakAmplitude,
            gate.SoundPathMeters * 1000.0,
            gate.PeakTimeSeconds * 1e6);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            Thread.Sleep(20);
        }
    }

    private static void WriteReport(string reportPath, IEnumerable<string> lines)
    {
        string[] content = lines.Select(line => line.Replace("\r", string.Empty)).SelectMany(line => line.Split('\n')).ToArray();
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(reportPath, content);
        }

        foreach (string line in content)
        {
            Console.WriteLine(line);
        }
    }
}
