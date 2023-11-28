using ServiceConnect.Interfaces;
using System.Diagnostics;
using System;

namespace ServiceConnect.Core.Telemetry;

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
}