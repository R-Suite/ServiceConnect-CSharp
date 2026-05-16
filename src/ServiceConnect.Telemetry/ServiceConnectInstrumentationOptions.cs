using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Options for <see cref="ServiceConnectActivitySource"/> telemetry generation.
/// </summary>
public sealed class ServiceConnectInstrumentationOptions
{
    private bool _frozen;
    private Action<Activity, Message>? _enrichWithMessage;
    private Action<Activity, byte[]>? _enrichWithMessageBytes;
    private bool _enablePublishTelemetry = true;
    private bool _enableConsumeTelemetry = true;
    private bool _enableSendTelemetry = true;
    private int _maxTagValueLength = 256;
    private Func<Exception, string>? _exceptionMessageSanitiser;

    /// <summary>
    /// Latches this options object so any further setter call throws <see cref="InvalidOperationException"/>.
    /// Called by <see cref="TelemetryBuilderExtensions.AddTelemetry"/> after the user's configure callback returns.
    /// </summary>
    internal void Freeze() => _frozen = true;

    private void ThrowIfFrozen([System.Runtime.CompilerServices.CallerMemberName] string? memberName = null)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"ServiceConnectInstrumentationOptions is frozen — '{memberName}' cannot be modified after AddTelemetry has returned. " +
                "Configure all properties inside the AddTelemetry callback.");
        }
    }

    /// <summary>
    /// Gets or sets an action to enrich an Activity from a message.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Activity"/>: the activity being enriched.</para>
    /// <para><see cref="Message"/>: the message being published/consumed.</para>
    /// <para>
    /// SECURITY WARNING: do not add raw payload fields as span tags without review —
    /// message bodies may contain PII, secrets, or regulated data that would then be
    /// exported to your OTel collector / downstream observability backends.
    /// </para>
    /// </remarks>
    public Action<Activity, Message>? EnrichWithMessage
    {
        get => _enrichWithMessage;
        set { ThrowIfFrozen(); _enrichWithMessage = value; }
    }

    /// <summary>
    /// Gets or sets an action to enrich an Activity from message bytes.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Activity"/>: the activity being enriched.</para>
    /// <para><see cref="byte"/>[]: the raw bytes of the message being published/consumed.</para>
    /// <para>
    /// SECURITY WARNING: do not attach raw bytes or decoded payload as span tags —
    /// the message body may contain PII, secrets, or regulated data that would then be
    /// exported to your OTel collector / downstream observability backends.
    /// </para>
    /// </remarks>
    public Action<Activity, byte[]>? EnrichWithMessageBytes
    {
        get => _enrichWithMessageBytes;
        set { ThrowIfFrozen(); _enrichWithMessageBytes = value; }
    }

    /// <summary>
    /// If set to true, the instrumentation will collect telemetry information for publish commands.
    /// </summary>
    public bool EnablePublishTelemetry
    {
        get => _enablePublishTelemetry;
        set { ThrowIfFrozen(); _enablePublishTelemetry = value; }
    }

    /// <summary>
    /// If set to true, the instrumentation will collect telemetry information for consume commands.
    /// </summary>
    public bool EnableConsumeTelemetry
    {
        get => _enableConsumeTelemetry;
        set { ThrowIfFrozen(); _enableConsumeTelemetry = value; }
    }

    /// <summary>
    /// If set to true, the instrumentation will collect telemetry information for send commands.
    /// </summary>
    public bool EnableSendTelemetry
    {
        get => _enableSendTelemetry;
        set { ThrowIfFrozen(); _enableSendTelemetry = value; }
    }

    /// <summary>
    /// Maximum length, in characters, of user-controlled string values written as activity tags
    /// (destination, routing key, MessageId, conversation id). Values exceeding this length are
    /// truncated. Defaults to 256. Set to <see cref="int.MaxValue"/> to disable truncation.
    /// </summary>
    public int MaxTagValueLength
    {
        get => _maxTagValueLength;
        set { ThrowIfFrozen(); _maxTagValueLength = value; }
    }

    /// <summary>
    /// Optional sanitiser invoked on exception messages before they are written to
    /// activity status descriptions and "exception.message" event tags. Use to redact
    /// PII or sensitive content. Returns the message to record. If null (default),
    /// the raw <see cref="Exception.Message"/> is recorded.
    /// </summary>
    public Func<Exception, string>? ExceptionMessageSanitiser
    {
        get => _exceptionMessageSanitiser;
        set { ThrowIfFrozen(); _exceptionMessageSanitiser = value; }
    }
}
