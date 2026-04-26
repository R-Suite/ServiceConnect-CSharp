namespace ServiceConnect.Interfaces;

/// <summary>
/// Base contract for persisted process-manager state.
/// </summary>
public interface IProcessManagerData
{
    /// <summary>
    /// Gets or sets the correlation id that identifies the process instance.
    /// </summary>
    Guid CorrelationId { get; set; }
}
