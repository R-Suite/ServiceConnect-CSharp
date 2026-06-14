using MongoDB.Driver;
using RabbitMQ.Client;

namespace ServiceConnect.Examples.Support.Bootstrap;

public static class DependencyWaiter
{
    public static Task WaitForRabbitMqAsync(
        string host,
        int port,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return WaitForAsync(
            async () =>
            {
                var factory = new ConnectionFactory
                {
                    HostName = host,
                    Port = port,
                    UserName = username,
                    Password = password
                };

                await using var connection = await factory.CreateConnectionAsync(cancellationToken);
            },
            "Timed out waiting for RabbitMQ.",
            IsPermanentRabbitMqFailure,
            cancellationToken);
    }

    public static Task WaitForMongoDbAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return WaitForAsync(
            async () =>
            {
                var client = new MongoClient(connectionString);
                using var cursor = await client.ListDatabaseNamesAsync(cancellationToken: cancellationToken);
                await cursor.MoveNextAsync(cancellationToken);
            },
            "Timed out waiting for MongoDB.",
            IsPermanentMongoDbFailure,
            cancellationToken);
    }

    private static async Task WaitForAsync(
        Func<Task> probe,
        string timeoutMessage,
        Func<Exception, bool> isPermanentFailure,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await probe();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (isPermanentFailure(ex))
                {
                    throw;
                }

                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        throw new TimeoutException(timeoutMessage, lastException);
    }

    private static bool IsPermanentRabbitMqFailure(Exception exception)
    {
        return exception is ArgumentException or FormatException
            || ContainsException(exception, "AuthenticationFailureException")
            || ContainsException(exception, "PossibleAuthenticationFailureException");
    }

    private static bool IsPermanentMongoDbFailure(Exception exception)
    {
        return exception is ArgumentException or FormatException or MongoConfigurationException
            || ContainsException(exception, nameof(MongoAuthenticationException))
            || exception is MongoCommandException { Code: 13 or 18 };
    }

    private static bool ContainsException(Exception exception, string typeName)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(current.GetType().Name, typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
