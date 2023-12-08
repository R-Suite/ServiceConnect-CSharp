using System;
using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect;

/// <summary>
/// Options for <see cref="ServiceConnectInstrumentation"/>.
/// </summary>
public class ServiceConnectInstrumentationOptions
{
    /// <summary>
    /// Gets or sets an action to enrich an Activity from message.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Activity"/>: the activity being enriched.</para>
    /// <para><see cref="Message"/>: the message being published/consumed.</para>
    /// </remarks>
    public Action<Activity, Message> EnrichWithMessage { get; set; }

    /// <summary>
    /// Gets or sets an action to enrich an Activity from message.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Activity"/>: the activity being enriched.</para>
    /// <para><see cref="byte"/>[]: the data of the message being published/consumed.</para>
    /// </remarks>
    public Action<Activity, byte[]> EnrichWithMessageBytes { get; set; }

    /// <summary>
    /// If set to true, the instrumentation will collect telemetry information for publish commands.
    /// </summary>
    public bool EnablePublishTelemetry { get; set; } = true;

    /// <summary>
    /// If set to true, the instrumentation will collect telemetry information for consume commands.
    /// </summary>
    public bool EnableConsumeTelemetry { get; set; } = true;

    /// <summary>
    /// If set to true, the instrumentation will collect telemetry information for send commands.
    /// </summary>
    public bool EnableSendTelemetry { get; set; } = true;
}