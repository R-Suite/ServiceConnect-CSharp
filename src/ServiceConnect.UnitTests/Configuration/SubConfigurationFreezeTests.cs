using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

/// <summary>
/// Verifies that all sub-configurations (Transport, Queues, Persistence, Pipeline) throw
/// after <see cref="BusConfiguration.Freeze"/> is called, and remain mutable before it.
/// </summary>
public class SubConfigurationFreezeTests
{
    // ── TransportConfiguration ──────────────────────────────────────────────

    [Fact]
    public void TransportConfiguration_Host_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.Host = "other");
    }

    [Fact]
    public void TransportConfiguration_Username_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.Username = "u");
    }

    [Fact]
    public void TransportConfiguration_Password_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.Password = "p");
    }

    [Fact]
    public void TransportConfiguration_RetryDelay_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.RetryDelay = 1000);
    }

    [Fact]
    public void TransportConfiguration_MaxRetries_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.MaxRetries = 5);
    }

    [Fact]
    public void TransportConfiguration_PrefetchCount_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.PrefetchCount = 2);
    }

    [Fact]
    public void TransportConfiguration_SslEnabled_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.SslEnabled = false);
    }

    [Fact]
    public void TransportConfiguration_SetClientSetting_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Transport.SetClientSetting("k", "v"));
    }

    [Fact]
    public void TransportConfiguration_IsMutableBeforeFreeze()
    {
        var config = new BusConfiguration();
        config.Transport.Host = "myhost";
        config.Transport.MaxRetries = 10;
        config.Transport.SetClientSetting("k", "v");

        Assert.Equal("myhost", config.Transport.Host);
        Assert.Equal(10, config.Transport.MaxRetries);
        Assert.Single(config.Transport.ClientSettings);
    }

    // ── QueueConfiguration ──────────────────────────────────────────────────

    [Fact]
    public void QueueConfiguration_QueueName_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.QueueName = "x");
    }

    [Fact]
    public void QueueConfiguration_ErrorQueueName_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.ErrorQueueName = "e");
    }

    [Fact]
    public void QueueConfiguration_AuditQueueName_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.AuditQueueName = "a");
    }

    [Fact]
    public void QueueConfiguration_AuditingEnabled_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.AuditingEnabled = true);
    }

    [Fact]
    public void QueueConfiguration_DisableErrors_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.DisableErrors = true);
    }

    [Fact]
    public void QueueConfiguration_PurgeQueueOnStartup_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.PurgeQueueOnStartup = true);
    }

    [Fact]
    public void QueueConfiguration_AddQueueMapping_SingleQueue_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.AddQueueMapping(typeof(string), "q"));
    }

    [Fact]
    public void QueueConfiguration_AddQueueMapping_ListOfQueues_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Queues.AddQueueMapping(typeof(string), ["q1", "q2"]));
    }

    [Fact]
    public void QueueConfiguration_IsMutableBeforeFreeze()
    {
        var config = new BusConfiguration();
        config.Queues.QueueName = "my-queue";
        config.Queues.AddQueueMapping(typeof(string), "q");

        Assert.Equal("my-queue", config.Queues.QueueName);
        Assert.Single(config.Queues.QueueMappings);
    }

    // ── PersistenceConfiguration ─────────────────────────────────────────────

    [Fact]
    public void PersistenceConfiguration_ConnectionString_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Persistence.ConnectionString = "conn");
    }

    [Fact]
    public void PersistenceConfiguration_DatabaseName_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Persistence.DatabaseName = "db");
    }

    [Fact]
    public void PersistenceConfiguration_AggregatorCollectionName_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Persistence.AggregatorCollectionName = "col");
    }

    [Fact]
    public void PersistenceConfiguration_IsMutableBeforeFreeze()
    {
        var config = new BusConfiguration();
        config.Persistence.ConnectionString = "mongodb://host/";
        config.Persistence.DatabaseName = "mydb";

        Assert.Equal("mongodb://host/", config.Persistence.ConnectionString);
        Assert.Equal("mydb", config.Persistence.DatabaseName);
    }

    // ── PipelineConfiguration ────────────────────────────────────────────────

    [Fact]
    public void PipelineConfiguration_BeforeConsumingFilters_Add_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Pipeline.BeforeConsumingFilters.Add(typeof(string)));
    }

    [Fact]
    public void PipelineConfiguration_AfterConsumingFilters_Add_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Pipeline.AfterConsumingFilters.Add(typeof(string)));
    }

    [Fact]
    public void PipelineConfiguration_OutgoingFilters_Add_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Pipeline.OutgoingFilters.Add(typeof(string)));
    }

    [Fact]
    public void PipelineConfiguration_MessageProcessingMiddleware_Add_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Pipeline.MessageProcessingMiddleware.Add(typeof(string)));
    }

    [Fact]
    public void PipelineConfiguration_SendMessageMiddleware_Add_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Pipeline.SendMessageMiddleware.Add(typeof(string)));
    }

    [Fact]
    public void PipelineConfiguration_OnConsumedSuccessfullyFilters_Add_AfterFreeze_Throws()
    {
        var config = new BusConfiguration();
        config.Freeze();
        Assert.Throws<InvalidOperationException>(() => config.Pipeline.OnConsumedSuccessfullyFilters.Add(typeof(string)));
    }

    [Fact]
    public void PipelineConfiguration_IsMutableBeforeFreeze()
    {
        var config = new BusConfiguration();
        config.Pipeline.BeforeConsumingFilters.Add(typeof(string));
        config.Pipeline.OutgoingFilters.Add(typeof(int));

        Assert.Single(config.Pipeline.BeforeConsumingFilters);
        Assert.Single(config.Pipeline.OutgoingFilters);
    }

    [Fact]
    public void PipelineConfiguration_FrozenListsAreReadableAfterFreeze()
    {
        var config = new BusConfiguration();
        config.Pipeline.BeforeConsumingFilters.Add(typeof(string));
        config.Freeze();

        // Reads must still work after freeze.
        Assert.Single(config.Pipeline.BeforeConsumingFilters);
        Assert.Equal(typeof(string), config.Pipeline.BeforeConsumingFilters[0]);
    }
}
