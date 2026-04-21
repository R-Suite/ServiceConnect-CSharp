using System;
using System.Collections.Generic;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class QueueConfigurationTests
    {
        [Fact]
        public void DefaultQueueNameIsEmpty()
        {
            var config = new QueueConfiguration();
            Assert.Equal("", config.QueueName);
        }

        [Fact]
        public void DefaultErrorQueueNameIsErrors()
        {
            var config = new QueueConfiguration();
            Assert.Equal("errors", config.ErrorQueueName);
        }

        [Fact]
        public void DefaultAuditQueueNameIsAudit()
        {
            var config = new QueueConfiguration();
            Assert.Equal("audit", config.AuditQueueName);
        }

        [Fact]
        public void DefaultAuditingEnabledIsFalse()
        {
            var config = new QueueConfiguration();
            Assert.False(config.AuditingEnabled);
        }

        [Fact]
        public void DefaultDisableErrorsIsFalse()
        {
            var config = new QueueConfiguration();
            Assert.False(config.DisableErrors);
        }

        [Fact]
        public void DefaultPurgeQueueOnStartupIsFalse()
        {
            var config = new QueueConfiguration();
            Assert.False(config.PurgeQueueOnStartup);
        }

        [Fact]
        public void DefaultQueueMappingsIsEmptyDictionary()
        {
            var config = new QueueConfiguration();
            Assert.NotNull(config.QueueMappings);
            Assert.Empty(config.QueueMappings);
        }

        [Fact]
        public void AddQueueMappingSingleQueue_CreatesEntryForType()
        {
            var config = new QueueConfiguration();
            config.AddQueueMapping(typeof(string), "queue1");

            Assert.True(config.QueueMappings.ContainsKey(typeof(string).AssemblyQualifiedName!));
            Assert.Single(config.QueueMappings[typeof(string).AssemblyQualifiedName!]);
            Assert.Equal("queue1", config.QueueMappings[typeof(string).AssemblyQualifiedName!][0]);
        }

        [Fact]
        public void AddQueueMappingSingleQueue_AppendsToPreviousMappings()
        {
            var config = new QueueConfiguration();
            config.AddQueueMapping(typeof(string), "queue1");
            config.AddQueueMapping(typeof(string), "queue2");

            var queues = config.QueueMappings[typeof(string).AssemblyQualifiedName!];
            Assert.Equal(2, queues.Count);
            Assert.Contains("queue1", queues);
            Assert.Contains("queue2", queues);
        }

        [Fact]
        public void AddQueueMappingListOfQueues_CreatesEntryForType()
        {
            var config = new QueueConfiguration();
            config.AddQueueMapping(typeof(int), new List<string> { "queueA", "queueB" });

            var key = typeof(int).AssemblyQualifiedName!;
            Assert.True(config.QueueMappings.ContainsKey(key));
            Assert.Equal(2, config.QueueMappings[key].Count);
            Assert.Contains("queueA", config.QueueMappings[key]);
            Assert.Contains("queueB", config.QueueMappings[key]);
        }

        [Fact]
        public void AddQueueMappingListOfQueues_AppendsToPreviousMappings()
        {
            var config = new QueueConfiguration();
            config.AddQueueMapping(typeof(int), new List<string> { "queueA" });
            config.AddQueueMapping(typeof(int), new List<string> { "queueB", "queueC" });

            var queues = config.QueueMappings[typeof(int).AssemblyQualifiedName!];
            Assert.Equal(3, queues.Count);
            Assert.Contains("queueA", queues);
            Assert.Contains("queueB", queues);
            Assert.Contains("queueC", queues);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void AddQueueMappingListOfQueues_RejectsInvalidElement(string? badQueue)
        {
            // M2 regression: previously only the list reference was validated, so null/
            // empty/whitespace entries were silently stored — the single-queue overload
            // already guarded this, leaving the list form inconsistent.
            var config = new QueueConfiguration();
            Assert.Throws<ArgumentException>(
                () => config.AddQueueMapping(typeof(int), new List<string> { "ok", badQueue! }));
        }

        [Fact]
        public void AddQueueMappingSingleQueue_DifferentTypesCreateSeparateEntries()
        {
            var config = new QueueConfiguration();
            config.AddQueueMapping(typeof(string), "string-queue");
            config.AddQueueMapping(typeof(int), "int-queue");

            Assert.Equal(2, config.QueueMappings.Count);
            Assert.Equal("string-queue", config.QueueMappings[typeof(string).AssemblyQualifiedName!][0]);
            Assert.Equal("int-queue", config.QueueMappings[typeof(int).AssemblyQualifiedName!][0]);
        }

        [Fact]
        public void PropertiesAreSettable()
        {
            var config = new QueueConfiguration
            {
                QueueName = "my-queue",
                ErrorQueueName = "my-errors",
                AuditQueueName = "my-audit",
                AuditingEnabled = true,
                DisableErrors = true,
                PurgeQueueOnStartup = true
            };

            Assert.Equal("my-queue", config.QueueName);
            Assert.Equal("my-errors", config.ErrorQueueName);
            Assert.Equal("my-audit", config.AuditQueueName);
            Assert.True(config.AuditingEnabled);
            Assert.True(config.DisableErrors);
            Assert.True(config.PurgeQueueOnStartup);
        }
    }
}
