namespace ServiceConnect.Client.RabbitMQ;

// Sentinel for the channel-null TOCTOU between EnsureConnectedAsync (run outside
// _publishLock / _connectionSemaphore) and the point where a caller actually uses
// the channel. A concurrent TearDownChannelAndConnectionAsync nulls _model at any time;
// throwing this type instead of NullReferenceException lets the retry classifier in
// ExecuteRetryingPublishAsync skip MarkResetRequired — the concurrent teardown is already
// the reset.
internal sealed class ChannelTransientException(string message) : Exception(message);
