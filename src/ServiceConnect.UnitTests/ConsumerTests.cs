using System.Collections.Generic;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class ConsumerTests
    {
        private readonly Mock<IServiceConnectConnection> _mockConnection;
        private readonly Mock<IModel> _mockModel;
        private Mock<ILogger> _mockLogger;

        public ConsumerTests()
        {
            _mockModel = new Mock<IModel>();
            _mockConnection = new Mock<IServiceConnectConnection>();
            _mockConnection.Setup(i => i.Connect());
            _mockConnection.Setup(i => i.CreateModel()).Returns(_mockModel.Object);
            _mockLogger = new Mock<ILogger>();
        }

        [Fact]
        public void TestConsumerWithDefaultSettings()
        {
            // Arrange
            IConsumer consumer = new Consumer(_mockConnection.Object, _mockLogger.Object);

            IConfiguration config = new Configuration();
            config.TransportSettings = new TransportSettings {ErrorQueueName = "myQueue.Errors", AuditQueueName = "myQueue.Audit" };
            config.TransportSettings.ClientSettings = new Dictionary<string, object>();


            // Act
            consumer.StartConsuming("myQueue", new List<string>(), null, config);


            // Assert
            _mockModel.Verify(x => x.QueueDeclare("myQueue", true, false, false, It.IsAny<Dictionary<string, object>>()), Times.Once);
            _mockModel.Verify(x => x.ExchangeDeclare("myQueue.Retries.DeadLetter", "direct", true, false, null), Times.Once);
            _mockModel.Verify(x => x.QueueDeclare("myQueue.Retries", true, false, false, It.IsAny<Dictionary<string, object>>()), Times.Once);
            _mockModel.Verify(x => x.ExchangeDeclare("myQueue.Errors", "direct", false, false, null), Times.Once);
            _mockModel.Verify(x => x.QueueDeclare("myQueue.Errors", true, false, false, It.IsAny<Dictionary<string, object>>()), Times.Once);
            _mockModel.Verify(x => x.QueueDeclare("myQueue.Audit", true, false, false, It.IsAny<Dictionary<string, object>>()), Times.Never);
        }

        [Fact]
        public void TestConsumerDeclaresAuditQueue()
        {
            // Arrange
            IConsumer consumer = new Consumer(_mockConnection.Object, _mockLogger.Object);

            IConfiguration config = new Configuration();
            config.TransportSettings = new TransportSettings { ErrorQueueName = "myQueue.Errors", AuditQueueName = "myQueue.Audit", AuditingEnabled = true };
            config.TransportSettings.ClientSettings = new Dictionary<string, object>();


            // Act
            consumer.StartConsuming("myQueue", new List<string>(), null, config);


            // Assert
            _mockModel.Verify(x => x.ExchangeDeclare("myQueue.Audit", "direct", false, false, null), Times.Once);
            _mockModel.Verify(x => x.QueueDeclare("myQueue.Audit", true, false, false, It.IsAny<Dictionary<string, object>>()), Times.Once);
        }

        [Fact]
        public void TestConsumerPurgesQueueOnStartup()
        {
            // Arrange
            IConsumer consumer = new Consumer(_mockConnection.Object, _mockLogger.Object);

            IConfiguration config = new Configuration();
            config.TransportSettings = new TransportSettings { PurgeQueueOnStartup = true };
            config.TransportSettings.ClientSettings = new Dictionary<string, object>();


            // Act
            consumer.StartConsuming("myQueue", new List<string>(), null, config);


            // Assert
            _mockModel.Verify(x => x.QueuePurge("myQueue"), Times.Once);
        }

        [Fact]
        public void TestConsumerConsumesMessageType()
        {
            // Arrange
            IConsumer consumer = new Consumer(_mockConnection.Object, _mockLogger.Object);

            IConfiguration config = new Configuration();
            config.TransportSettings = new TransportSettings { PurgeQueueOnStartup = true };
            config.TransportSettings.ClientSettings = new Dictionary<string, object>();


            // Act
            consumer.StartConsuming("myQueue", new List<string> {"MyMessageType1"}, null, config);


            // Assert
            _mockModel.Verify(x => x.ExchangeDeclare("MyMessageType1", "fanout", true, false, null), Times.Once);
            _mockModel.Verify(x => x.QueueBind("myQueue", "MyMessageType1", string.Empty, null), Times.Once);
        }
    }
}
