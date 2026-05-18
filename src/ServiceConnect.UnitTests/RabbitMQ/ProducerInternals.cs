using System;
using System.Reflection;
using ServiceConnect.Client.RabbitMQ;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Reflection helpers for the Producer test suite. Producer's internal state is split
/// across Producer and the nested ProducerConnection collaborator; lookups fall back to
/// the connection collaborator so tests that reflect on the older flat layout
/// (e.g. <c>_model</c>, <c>_connected</c>, <c>_declaredExchanges</c>) still resolve to
/// the right object without touching every call site.
/// </summary>
internal static class ProducerInternals
{
    public static void SetField<T>(Producer producer, string fieldName, T value)
    {
        var (target, field) = Resolve(producer, fieldName);
        field.SetValue(target, value);
    }

    public static T GetField<T>(Producer producer, string fieldName)
    {
        var (target, field) = Resolve(producer, fieldName);
        return (T)field.GetValue(target)!;
    }

    private static (object Target, FieldInfo Field) Resolve(Producer producer, string fieldName)
    {
        var direct = typeof(Producer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (direct != null)
        {
            return (producer, direct);
        }

        // Field migrated to ProducerConnection — walk into Producer's _producerConnection collaborator.
        var connectionField = typeof(Producer).GetField("_producerConnection", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Producer._producerConnection not found; ProducerInternals needs updating.");
        var connection = connectionField.GetValue(producer)
            ?? throw new InvalidOperationException("Producer._producerConnection is null.");

        var inner = connection.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Field '{fieldName}' not found on Producer or ProducerConnection.");
        return (connection, inner);
    }
}
