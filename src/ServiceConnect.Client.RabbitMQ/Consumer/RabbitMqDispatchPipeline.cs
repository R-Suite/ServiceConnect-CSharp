using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Diagnostics;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Inner dispatch + ack/nack stage for a single RabbitMQ consumer host. Wraps the
/// admitted, validated delivery's invocation of <see cref="InboundMessageProcessor.ProcessAsync"/>
/// with the messaging.process.* metrics, then drives the ack/nack decision against the
/// model channel based on the handler outcome and the channel/shutdown state.
/// </summary>
internal sealed class RabbitMqDispatchPipeline(
    string consumerQueueName,
    Func<bool> shutdownTimedOutQuery,
    Func<bool> shutdownStartedQuery,
    ILogger logger)
{
    private readonly string _consumerQueueName = consumerQueueName ?? throw new ArgumentNullException(nameof(consumerQueueName));
    private readonly Func<bool> _shutdownTimedOutQuery = shutdownTimedOutQuery ?? throw new ArgumentNullException(nameof(shutdownTimedOutQuery));
    private readonly Func<bool> _shutdownStartedQuery = shutdownStartedQuery ?? throw new ArgumentNullException(nameof(shutdownStartedQuery));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs the handler dispatch (with messaging.process.* metric instrumentation) and the
    /// subsequent ack/nack against the model channel. Returns nothing — the caller's
    /// responsibility ends after this method, which has already emitted all metrics and
    /// driven the broker frame for this delivery.
    /// </summary>
    public async Task DispatchAndAckAsync(
        InboundMessageProcessor processor,
        IChannel? model,
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        CancellationToken cancellationToken)
    {
        bool processed = await ProcessWithMetricsAsync(processor, publishChannel, args, cancellationToken).ConfigureAwait(false);
        await AckOrNackAsync(model, args, processed).ConfigureAwait(false);
    }

    /// <summary>
    /// Direct ack/nack against the model channel, used by callers that already know the
    /// outcome and don't need handler dispatch / metric emission. Validator-rejection passes
    /// <paramref name="processed"/>=true (ack-and-don't-redeliver: redelivery would just hit
    /// the same rule); the null-processor branch passes <paramref name="processed"/>=false
    /// (nack-with-requeue: handler will be available on the next consumer start).
    /// </summary>
    public async Task AckOrNackAsync(IChannel? model, BasicDeliverEventArgs args, bool processed)
    {
        try
        {
            if (model == null)
            {
                // Channel was nulled by concurrent DisposeAsync. Expected during teardown;
                // broker will redeliver unacked messages on next consumer start.
                _logger.LogDebug("Channel was null during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
            }
            else if (!model.IsOpen)
            {
                // Channel closed concurrently. Expected during teardown / connection drop.
                _logger.LogDebug("Channel was closed during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
            }
            else if (_shutdownTimedOutQuery())
            {
                _logger.LogDebug("Shutdown grace window expired before finishing message {DeliveryTag}; leaving unacked for broker redelivery", args.DeliveryTag);
            }
            else if (processed)
            {
                await model.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
            }
            else
            {
                await model.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
            }
        }
        catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException ex)
        {
            // Expected when the connection/channel is torn down concurrently with
            // message processing (typical during shutdown). The broker will redeliver
            // unacked messages after the connection drops, so this is not an error.
            if (_shutdownStartedQuery())
            {
                _logger.LogDebug(ex, "Channel already closed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
            }
            else
            {
                LogAckOrNackFailure(ex, args, processed);
            }
        }
        catch (ObjectDisposedException ex)
        {
            if (_shutdownStartedQuery())
            {
                _logger.LogDebug(ex, "Channel disposed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
            }
            else
            {
                LogAckOrNackFailure(ex, args, processed);
            }
        }
        catch (Exception ex)
        {
            LogAckOrNackFailure(ex, args, processed);
        }
    }

    // Inner metric scope: only the ProcessAsync invocation itself; admission/header validation
    // are operator-visible failures of THIS host, not handler failures, so they don't show up
    // on the messaging.process.* metrics. Returns the same processed flag the caller would
    // otherwise have assigned, with handler exceptions logged-and-swallowed exactly as the
    // pre-metrics path did so the outer ack/nack finally still drives the requeue decision.
    private async Task<bool> ProcessWithMetricsAsync(
        InboundMessageProcessor processor,
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        CancellationToken cancellationToken)
    {
        var processStartTimestamp = Stopwatch.GetTimestamp();
        bool processed = false;
        Exception? processFailure = null;
        try
        {
            processed = await processor.ProcessAsync(publishChannel, args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            processFailure = ex;
            _logger.LogError(ex, "Error processing message");
        }
        finally
        {
            EmitProcessMetrics(processStartTimestamp, processed, processFailure);
        }

        return processed;
    }

    // Emits messaging.process.duration (always) and messaging.client.consumed.messages
    // (always, tagged by outcome). Outcome is one of:
    //   success — handler returned and ProcessAsync routed it through the success/audit path.
    //   error   — ProcessAsync threw or the host caught a handler exception.
    //   retry   — handler returned a non-success ConsumeEventResult; the message was routed
    //             to the retry queue, so processed=false but no exception was thrown.
    // The retry-publish-failure swallow at InboundMessageProcessor (catch (Exception retryEx))
    // also surfaces here as outcome=success because ProcessAsync still returns true: that drop
    // is reported separately on messaging.serviceconnect.retry.drops in a later commit.
    private void EmitProcessMetrics(long startTimestamp, bool processed, Exception? processFailure)
    {
        // Cache the mapped error type once — used on both the duration histogram and the
        // consumed-messages counter when the handler threw. ExceptionTypeMapper.Map performs
        // a virtual call + switch, so caching avoids a redundant lookup per emit pair.
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        var errorType = processFailure is null ? null : ExceptionTypeMapper.Map(processFailure);

        var processTags = new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.operation.type", "process" },
            { "messaging.operation.name", "process" },
            { "messaging.destination.name", _consumerQueueName },
        };
        if (errorType is not null)
        {
            processTags.Add("error.type", errorType);
        }
        ServiceConnectMeter.RecordProcessDuration(elapsed, processTags);

        string outcome;
        if (processFailure != null)
        {
            outcome = "error";
        }
        else if (processed)
        {
            outcome = "success";
        }
        else
        {
            outcome = "retry";
        }

        var consumedTags = new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.operation.type", "process" },
            { "messaging.operation.name", "process" },
            { "messaging.destination.name", _consumerQueueName },
            { "messaging.outcome", outcome },
        };
        if (errorType is not null)
        {
            consumedTags.Add("error.type", errorType);
        }
        ServiceConnectMeter.AddConsumedMessage(consumedTags);
    }

    // Emit AckFailed when we were trying to ack (processed=true) and NackFailed when we
    // were trying to nack-with-requeue (processed=false). Carries MessageId from
    // BasicProperties.MessageId so log readers can correlate to a specific message;
    // falls back to DeliveryTag when the producer didn't stamp a MessageId.
    private void LogAckOrNackFailure(Exception ex, BasicDeliverEventArgs args, bool processed)
    {
        var messageId = string.IsNullOrEmpty(args.BasicProperties.MessageId)
            ? args.DeliveryTag.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : args.BasicProperties.MessageId;
        if (processed)
        {
            RabbitMqClientLog.AckFailed(_logger, ex, messageId, args.DeliveryTag, _consumerQueueName);
        }
        else
        {
            RabbitMqClientLog.NackFailed(_logger, ex, messageId, args.DeliveryTag, _consumerQueueName);
        }
    }
}
