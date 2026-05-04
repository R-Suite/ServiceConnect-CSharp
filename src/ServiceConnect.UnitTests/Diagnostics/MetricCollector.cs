using System.Diagnostics.Metrics;
using ServiceConnect.Diagnostics;

namespace ServiceConnect.UnitTests.Diagnostics;

/// <summary>
/// MeterListener-based helper for unit tests. Subscribes to ServiceConnect.Bus instruments
/// during the test's lifetime and exposes captured records.
/// </summary>
/// <remarks>
/// Tests that run in parallel and emit on the same instruments will pollute each other's
/// captures. Use the constructor overload that takes a tag-key/tag-value pair to filter the
/// listener at the source — a typical choice is <c>messaging.destination.name</c> with a
/// per-test unique queue/exchange name baked into the SUT configuration.
/// </remarks>
internal sealed class MetricCollector : IDisposable
{
    public sealed record Record<T>(string InstrumentName, T Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? GetTag(string key) => Tags.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    private readonly MeterListener _listener;
    private readonly List<Record<long>> _longRecords = [];
    private readonly List<Record<double>> _doubleRecords = [];
    private readonly string? _filterTagKey;
    private readonly string? _filterTagValue;

    /// <summary>Subscribes without filtering — captures every record on ServiceConnect.Bus.</summary>
    public MetricCollector() : this(filterTagKey: null, filterTagValue: null) { }

    /// <summary>Subscribes and only records measurements whose tags include the given key/value pair.</summary>
    public MetricCollector(string? filterTagKey, string? filterTagValue)
    {
        _filterTagKey = filterTagKey;
        _filterTagValue = filterTagValue;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!Matches(tags))
            {
                return;
            }
            lock (_longRecords)
            {
                _longRecords.Add(new(instrument.Name, value, ToDictionary(tags)));
            }
        });
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (!Matches(tags))
            {
                return;
            }
            lock (_doubleRecords)
            {
                _doubleRecords.Add(new(instrument.Name, value, ToDictionary(tags)));
            }
        });
        _listener.Start();
    }

    public IReadOnlyList<Record<long>> GetLongRecords(string instrumentName)
    {
        lock (_longRecords)
        {
            return [.. _longRecords.Where(r => r.InstrumentName == instrumentName)];
        }
    }

    public IReadOnlyList<Record<double>> GetDoubleRecords(string instrumentName)
    {
        lock (_doubleRecords)
        {
            return [.. _doubleRecords.Where(r => r.InstrumentName == instrumentName)];
        }
    }

    private bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (_filterTagKey is null)
        {
            return true;
        }
        foreach (var kv in tags)
        {
            if (kv.Key == _filterTagKey && (kv.Value as string) == _filterTagValue)
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var kv in tags)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }

    public void Dispose() => _listener.Dispose();
}
