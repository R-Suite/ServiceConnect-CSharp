using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IPersistenceConfiguration"/> used to configure
/// persistent ServiceConnect storage.
/// </summary>
/// <remarks>
/// <see cref="ConnectionString"/> defaults to <see cref="string.Empty"/>; callers must
/// explicitly configure it. The previous default (<c>"mongodb://localhost/"</c>) silently
/// targeted localhost when misconfigured — production deployments shipping with the default
/// value were a real accident-mode. Legacy callers that depended on the default must update
/// their <c>ConfigurePersistence(c =&gt; c.ConnectionString = "...")</c> wiring.
/// </remarks>
internal sealed class PersistenceConfiguration : IPersistenceConfiguration
{
    private bool _frozen;
    private string _connectionString = string.Empty;
    private string _databaseName = "RMessageBusPersistentStore";
    private string _aggregatorCollectionName = "Aggregator";

    /// <summary>
    /// Latches this configuration so further setter calls throw <see cref="System.InvalidOperationException"/>.
    /// Called by <see cref="BusConfiguration.Freeze"/> after the user's configure callback returns.
    /// </summary>
    internal void Freeze() => _frozen = true;

    private void ThrowIfFrozen([System.Runtime.CompilerServices.CallerMemberName] string? memberName = null)
    {
        if (_frozen)
        {
            throw new System.InvalidOperationException(
                $"PersistenceConfiguration is frozen — '{memberName}' cannot be modified after AddServiceConnect has returned. " +
                "Configure all properties inside the AddServiceConnect callback.");
        }
    }

    /// <summary>
    /// Provider-specific connection string. Required; no default. Misconfiguration
    /// surfaces as <see cref="System.InvalidOperationException"/> at first persistence
    /// use rather than silently targeting localhost.
    /// </summary>
    public string ConnectionString { get => _connectionString; set { ThrowIfFrozen(); _connectionString = value; } }
    /// <inheritdoc />
    public string DatabaseName { get => _databaseName; set { ThrowIfFrozen(); _databaseName = value; } }
    /// <inheritdoc />
    public string AggregatorCollectionName { get => _aggregatorCollectionName; set { ThrowIfFrozen(); _aggregatorCollectionName = value; } }
}
