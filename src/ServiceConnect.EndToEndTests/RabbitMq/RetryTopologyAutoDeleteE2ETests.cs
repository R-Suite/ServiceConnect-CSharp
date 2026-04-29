using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using Xunit;

namespace ServiceConnect.EndToEndTests.RabbitMq;

/// <summary>
/// Verifies the C12 contract: the retry DLX is declared with autoDelete:false and therefore
/// survives the auto-deletion of the main consumer queue. This matters because the retry queue
/// dead-letters messages back through the DLX after their TTL expires — if the DLX had been
/// deleted when the main queue dropped, the re-declared main queue could not be (re-)bound to it,
/// and the retried message would be silently dropped.
/// </summary>
[Collection(nameof(MessagingCollection))]
public sealed class RetryTopologyAutoDeleteE2ETests(MessagingFixture fixture)
{
    // RabbitMqQueueNaming is internal to ServiceConnect.Client.RabbitMQ; mirror the constants
    // rather than adding a test-only InternalsVisibleTo just for two string literals.
    private const string RetryQueueSuffix = ".Retries";
    private const string RetryDlxSuffix = ".Retries.DeadLetter";

    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RetryDlx_OutlivesAutoDeletedMainQueue_MessageRedeliversAfterMainQueueRecreate()
    {
        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };

        await using var connection = await factory.CreateConnectionAsync();

        var queueName = _fixture.GetUniqueQueueName("retry-dlx-survives");
        var retryQueueName = queueName + RetryQueueSuffix;
        var retryDlxName = queueName + RetryDlxSuffix;
        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);

        // Phase 1: declare the main queue with autoDelete:true, attach a consumer (autoDelete
        // fires when the last consumer disconnects), provision the retry topology, and publish
        // into the retry queue. ConfigureRetryTopologyAsync hard-codes autoDelete:false on the
        // DLX regardless of the autoDelete argument — that is the fix under test.
        await using (var setupChannel = await connection.CreateChannelAsync())
        {
            await setupChannel.QueueDeclareAsync(
                queueName,
                durable: false,
                exclusive: false,
                autoDelete: true,
                arguments: null);

            await provisioner.ConfigureRetryTopologyAsync(
                setupChannel,
                queueName,
                durable: false,
                autoDelete: true,
                retryDelayMs: 1000,
                retryQueueArguments: new Dictionary<string, object?>(),
                isInitialSetup: true);

            // Register a consumer so the queue is "active"; autoDelete will fire when this
            // consumer is gone (i.e., when setupChannel is disposed below).
            var placeholderConsumer = new AsyncEventingBasicConsumer(setupChannel);
            await setupChannel.BasicConsumeAsync(queueName, autoAck: true, placeholderConsumer);

            // Publish directly to the retry queue via the default exchange. The retry queue has
            // x-message-ttl=1000 ms and x-dead-letter-exchange=retryDlxName, so after 1000 ms
            // the broker dead-letters the message through the retry DLX with routing key
            // retryQueueName. The binding queueName ↔ retryDlxName (routing key retryQueueName)
            // that ConfigureRetryTopologyAsync created will route it back to the main queue.
            await setupChannel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: retryQueueName,
                mandatory: false,
                basicProperties: new BasicProperties { Persistent = false },
                body: new byte[] { 1, 2, 3 });
        }

        // setupChannel disposed here → its consumer is cancelled → main queue loses its last
        // consumer → autoDelete fires and the broker drops the main queue. The binding from the
        // main queue to the retry DLX therefore dissolves. Pre-fix the DLX would also have been
        // declared with autoDelete:true and would auto-delete when its binding count hit zero;
        // post-fix the DLX is declared with autoDelete:false and survives.

        // Allow up to 2 s for the broker to process the autoDelete on the main queue.
        var dropped = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2) && !dropped)
        {
            // Each OperationInterruptedException closes the channel, so open a fresh one per attempt.
            await using var checkChannel = await connection.CreateChannelAsync();
            try
            {
                await checkChannel.QueueDeclarePassiveAsync(queueName);
                await Task.Delay(100);
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
            {
                dropped = true;
            }
        }

        // Phase 2: re-declare the main queue and re-bind it to the retry DLX BEFORE the retry
        // queue's TTL fires. This is the realistic "consumer restart" scenario: the new consumer
        // process re-declares its queue and re-provisions topology on startup. The binding must
        // exist at the moment the TTL fires so the dead-lettered message can route through.
        //
        // Post-fix: the retry DLX is still alive (autoDelete:false), so QueueBindAsync succeeds.
        // Pre-fix: the retry DLX was auto-deleted when the main queue's binding dissolved, so
        // QueueBindAsync would fail with 404 — or, if the DLX declaration in
        // ConfigureRetryTopologyAsync re-created it silently, the message would still be lost
        // because dead-letter routing captures the DLX reference at enqueue time (when the
        // retry queue was first declared), and an auto-deleted+recreated exchange is a different
        // object with a different internal reference.
        await using var consumerChannel = await connection.CreateChannelAsync();
        await consumerChannel.QueueDeclareAsync(
            queueName,
            durable: false,
            exclusive: false,
            autoDelete: true,
            arguments: null);

        // Re-establish the binding the setup channel held; idempotent if the DLX survived.
        await consumerChannel.QueueBindAsync(queueName, retryDlxName, retryQueueName, null);

        var receivedTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(consumerChannel);
        consumer.ReceivedAsync += (_, args) =>
        {
            receivedTcs.TrySetResult(args.Body.ToArray());
            return Task.CompletedTask;
        };
        await consumerChannel.BasicConsumeAsync(queueName, autoAck: true, consumer);

        // Wait for the retry queue's TTL to fire (1000 ms) plus a safety margin.
        // Post-fix: the dead-lettered message routes through the surviving DLX to the
        //           re-declared main queue, and the consumer receives it.
        // Pre-fix:  the DLX was gone; QueueBindAsync above would have thrown (404), or the
        //           message is unroutable at TTL expiry and silently dropped.
        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }
}
