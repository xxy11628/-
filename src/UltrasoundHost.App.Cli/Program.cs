// Composition root: the only place that knows the host and every plugin.
// Runs the whole chain without hardware through the shared Application session:
// simulator device -> session pump -> gate processor -> snapshot.
using System.Globalization;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

int frameCount = 20;
int channelCount = 1;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames" when i + 1 < args.Length:
            frameCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--channels" when i + 1 < args.Length:
            channelCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--help":
            Console.WriteLine("usage: UltrasoundHost.App.Cli [--frames N] [--channels C]");
            return 0;
        default:
            Console.Error.WriteLine("unknown or incomplete argument: " + args[i]);
            return 2;
    }
}

try
{
    // The plugin list is the only hard-coded part. Replacing it with "scan a folder"
    // is the later step towards dynamic loading.
    var plugins = new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin() };
    await using var session = new ShellSession(plugins);
    await session.InitializeAsync();

    foreach (PluginStatus status in session.Statuses)
    {
        string reason = status.Reason is null ? string.Empty : " - " + status.Reason;
        Console.WriteLine("plugin " + status.Id.PadRight(20) + " " + status.State + reason);
    }

    SessionCheckResult check = await session.OpenDeviceAsync(channelCount);
    Console.WriteLine("device " + session.DeviceId + ": " + session.DeviceCapabilities?.ReceiveChannels
        + " receive channel(s), " + session.Probe?.Topology + " probe");

    foreach (SessionCheckItem item in check.Items)
    {
        string verdict = item.Satisfied ? "ok" : "NOT SATISFIED: " + string.Join("; ", item.Unmet);
        Console.WriteLine("session check " + item.PluginId.PadRight(20) + " " + verdict);
    }

    if (!check.AllSatisfied)
    {
        return 1;
    }

    AcquisitionParameters current = new AcquisitionParameters { Version = 1, ActiveReceiveChannels = channelCount };
    bool gainRaised = false;
    int printed = 0;

    session.InspectionCompleted += processed =>
    {
        if (Interlocked.Increment(ref printed) > frameCount)
        {
            return;
        }

        foreach (GateResult gate in processed.Gates)
        {
            string flag = gate.Triggered ? "TRIG" : " -- ";
            Console.WriteLine(
                "#" + processed.Sequence.ToString().PadLeft(4)
                + " v" + processed.ParamVersion
                + " ch" + gate.Channel
                + " " + flag
                + " amp=" + gate.PeakAmplitude.ToString("F3")
                + " depth=" + (gate.SoundPathMeters * 1000.0).ToString("F2").PadLeft(6) + " mm");
        }
    };

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await session.StartAcquisitionAsync(current, timeout.Token);

    try
    {
        while (session.FramesProcessed < frameCount)
        {
            await Task.Delay(10, timeout.Token);

            // Halfway through, change the gain. The version increases and in-flight frames keep
            // being processed with the parameters they were acquired under (parameter history).
            if (!gainRaised && session.FramesProcessed >= frameCount / 2)
            {
                gainRaised = true;
                current = current.Revise(previous => previous with { GainDb = 6 });
                await session.ApplyParametersAsync(current, timeout.Token);
                Console.WriteLine("-- gain raised to 6 dB, parameter version is now " + current.Version);
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("timed out after 30 s");
        await session.StopAcquisitionAsync();
        return 1;
    }

    await session.StopAcquisitionAsync();
    LossCounters counters = session.Counters;
    Console.WriteLine("done: " + frameCount + " frame(s) requested"
        + " | received " + counters.Received
        + " | inspection " + counters.Inspection.Processed + " processed/" + counters.Inspection.Dropped + " dropped"
        + " | display " + counters.Display.Processed + " processed/" + counters.Display.Dropped + " dropped"
        + " | gaps " + counters.SequenceGaps
        + " | incomplete " + (counters.InspectionIncomplete ? "yes" : "no"));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}
