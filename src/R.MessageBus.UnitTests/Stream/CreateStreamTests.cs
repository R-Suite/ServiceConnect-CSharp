using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.Settings;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Stream
{
    public class CreateStreamTests
    {

        [Fact]
        public void CreateStreamShouldCreateAMessageBusWriteStream()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockStream = new Mock<IMessageBusWriteStream>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(StreamResponseMessage).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            mockConfiguration.Setup(x=> x.GetMessageBusWriteStream(It.IsAny<IProducer>(), "TestEndpoint", It.IsAny<string>())).Returns(mockStream.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(It.IsAny<string>(), message, It.IsAny<Dictionary<string, string>>())).Callback(task.Start);

            // Act
            var bus = new Bus(mockConfiguration.Object);

            // Act
            var stream = bus.CreateStream("TestEndpoint", message);

            // Assert
            Assert.NotNull(stream);
            Assert.Equal(mockStream.Object, stream);
        }

        [Fact]
        public void CreateStreamShouldSendARequestMessageToTheSpecifiedEndpoint()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(StreamResponseMessage).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(It.IsAny<string>(), message, It.IsAny<Dictionary<string, string>>())).Callback(task.Start);

            // Act
            var bus = new Bus(mockConfiguration.Object);

            // Act
            bus.CreateStream("TestEndpoint", message);

            // Assert
            mockProducer.Verify(x => x.Send("TestEndpoint", message, It.Is<Dictionary<string, string>>(y => y["MessageType"] == "ByteStream" && y.ContainsKey("Start"))));
        }
    }
}