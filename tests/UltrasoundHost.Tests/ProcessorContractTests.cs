using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Processing.Basic;

namespace UltrasoundHost.Tests;

/// <summary>
/// Reusable processor contract suite (07 §4.3). A new processing plugin joins by inheriting this
/// class and providing frames/parameters.
/// </summary>
[Collection("FramePool")]
public abstract class ProcessorContractTestsBase
{
    protected abstract IFrameProcessor CreateProcessor();

    protected abstract AcquisitionParameters CreateParameters();

    protected abstract AcquisitionFrame CreateFrame(AcquisitionParameters parameters, AdcFormat adc);

    [Fact]
    public void PC1_processing_the_same_frame_twice_is_deterministic()
    {
        IFrameProcessor processor = CreateProcessor();
        AcquisitionParameters parameters = CreateParameters();
        using AcquisitionFrame frame = CreateFrame(parameters, AdcFormat.Default);

        ProcessedFrame first = processor.Process(frame, parameters);
        ProcessedFrame second = processor.Process(frame, parameters);

        Assert.Equal(first.Gates.ToArray(), second.Gates.ToArray());
    }

    [Fact]
    public void PC2_the_processor_does_not_modify_or_retain_the_frame()
    {
        IFrameProcessor processor = CreateProcessor();
        AcquisitionParameters parameters = CreateParameters();
        using AcquisitionFrame frame = CreateFrame(parameters, AdcFormat.Default);
        short[] before = frame.Data.ToArray();

        _ = processor.Process(frame, parameters);

        Assert.Equal(before, frame.Data.ToArray());
        Assert.Equal(1, frame.RefCount);
    }

    [Fact]
    public void PC3_a_frame_from_an_older_parameter_version_is_rejected()
    {
        IFrameProcessor processor = CreateProcessor();
        AcquisitionParameters parameters = CreateParameters();
        using AcquisitionFrame frame = CreateFrame(parameters, AdcFormat.Default);
        AcquisitionParameters newer = parameters.Revise(previous => previous with { GainDb = 3 });

        Assert.Throws<InvalidOperationException>(() => processor.Process(frame, newer));
    }

    [Fact]
    public void PC4_amplitude_does_not_depend_on_the_adc_bit_depth()
    {
        IFrameProcessor processor = CreateProcessor();
        AcquisitionParameters parameters = CreateParameters();
        using AcquisitionFrame frame16 = CreateFrame(parameters, AdcFormat.Default);
        using AcquisitionFrame frame12 = CreateFrame(parameters, new AdcFormat { Bits = 12 });

        GateResult at16Bits = processor.Process(frame16, parameters).Gates[0];
        GateResult at12Bits = processor.Process(frame12, parameters).Gates[0];

        Assert.InRange(at12Bits.PeakAmplitude, at16Bits.PeakAmplitude - 0.02, at16Bits.PeakAmplitude + 0.02);
    }
}

public sealed class GatePeakProcessorContractTests : ProcessorContractTestsBase
{
    protected override IFrameProcessor CreateProcessor() => new GatePeakProcessor();

    protected override AcquisitionParameters CreateParameters() => new AcquisitionParameters
    {
        Version = 1,
        SamplesPerRecord = 100,
        Gate = new GateConfig(0, 2e-6, 0.2),
    };

    protected override AcquisitionFrame CreateFrame(AcquisitionParameters parameters, AdcFormat adc)
    {
        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 2, parameters.SamplesPerRecord);
        frame.SampleRateHz = 50e6;
        frame.ParamVersion = parameters.Version;
        frame.Adc = adc;
        for (int channel = 0; channel < frame.Rx; channel++)
        {
            frame.Span(0, channel).Clear();
            frame.Span(0, channel)[10] = (short)Math.Round(0.25 * adc.FullScaleCounts);
        }

        return frame;
    }
}
