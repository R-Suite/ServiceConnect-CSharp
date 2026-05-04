using System.Diagnostics;
using System.Diagnostics.Metrics;
using ServiceConnect.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.Diagnostics;

public class ServiceConnectMeterTests
{
    [Fact]
    public void MeterName_Is_ServiceConnectBus()
    {
        Assert.Equal("ServiceConnect.Bus", ServiceConnectMeter.MeterName);
    }

    [Fact]
    public void RecordPublishDuration_EmitsOnPublishDurationInstrument()
    {
        var captured = new List<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            captured.Add((instrument.Name, value)));
        listener.Start();

        ServiceConnectMeter.RecordPublishDuration(0.123, new TagList());

        var (name, value) = Assert.Single(captured);
        Assert.Equal(MetricNames.PublishDuration, name);
        Assert.Equal(0.123, value);
    }

    [Fact]
    public void AddPublishedMessage_IncrementsPublishedMessagesCounter()
    {
        var captured = new List<(string Name, long Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            captured.Add((instrument.Name, value)));
        listener.Start();

        ServiceConnectMeter.AddPublishedMessage(new TagList());

        var (name, value) = Assert.Single(captured);
        Assert.Equal(MetricNames.PublishedMessages, name);
        Assert.Equal(1, value);
    }

    [Fact]
    public void AddInFlight_AdjustsUpDownCounterByDelta()
    {
        long total = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName
                    && instrument.Name == MetricNames.InFlightMessages)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => total += value);
        listener.Start();

        ServiceConnectMeter.AddInFlight(1, new TagList());
        ServiceConnectMeter.AddInFlight(1, new TagList());
        ServiceConnectMeter.AddInFlight(-1, new TagList());

        Assert.Equal(1, total);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), "cancelled")]
    [InlineData(typeof(TimeoutException), "timeout")]
    [InlineData(typeof(InvalidOperationException), "InvalidOperationException")]
    [InlineData(typeof(ArgumentException), "ArgumentException")]
    public void ExceptionTypeMapper_MapsKnownTypesAndFallsBackToShortName(Type exceptionType, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test")!;

        Assert.Equal(expected, ExceptionTypeMapper.Map(exception));
    }
}
