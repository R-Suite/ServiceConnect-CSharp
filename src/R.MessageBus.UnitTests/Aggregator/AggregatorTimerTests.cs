using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Moq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.Settings;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Aggregator
{
    public class AggregatorTimerTests
    {
        [Fact]
        public void ShouldStartAggregatorTimerIfAggregatorTimeoutIsSet()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();
            var mockAggregatorTImer = new Mock<IAggregatorTimer>();
            var mockConfiguration = new Mock<IConfiguration>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes()).Returns(new List<HandlerReference> { handlerRef });
            var timeout = new TimeSpan(0, 0, 0, 1);
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(new FakeAggregator
            {
                Time = timeout
            });
            mockConfiguration.Setup(x => x.GetAggregatorTimer(It.IsAny<IAggregatorPersistor>(), mockContainer.Object, typeof(FakeAggregator))).Returns(mockAggregatorTImer.Object);
            mockConfiguration.Setup(x => x.GetAggregatorPersistor()).Returns(mockAggregatorPersistor.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.AutoStartConsuming).Returns(true);
            mockConfiguration.Setup(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetConsumer()).Returns(new Mock<IConsumer>().Object);

            // Act
            new Bus(mockConfiguration.Object);

            // Assert
            mockAggregatorTImer.Verify(x => x.StartTimer<FakeMessage1>(timeout), Times.Once);
        }

        [Fact]
        public void TimerShouldRunEverySecond()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorTimer(mockPersistor.Object, mockContainer.Object, typeof (FakeAggregator));

            var count = 0;

            mockPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(0).Callback(() => count++);

            // Act
            timer.StartTimer<FakeMessage1>(new TimeSpan(0, 0, 0, 1));
            Thread.Sleep(2100);

            // Assert 
            mockPersistor.Verify(x => x.Count(typeof (FakeMessage1).AssemblyQualifiedName), Times.Exactly(2));
            Assert.Equal(2, count);

            timer.Dispose();
        }

        [Fact]
        public void TimerShouldGetMessagesFromAggregatorAndExecuteHandler()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorTimer(mockPersistor.Object, mockContainer.Object, typeof(FakeAggregator));

            mockPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(1);
            var aggregator = new FakeAggregator();
            var message = new FakeMessage1(Guid.NewGuid());
            mockContainer.Setup(x => x.GetInstance(typeof (FakeAggregator))).Returns(aggregator);
            mockPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>{ message });

            // Act
            timer.StartTimer<FakeMessage1>(new TimeSpan(0, 0, 0, 0, 50));
            Thread.Sleep(90);

            // Assert 
            mockPersistor.Verify(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName), Times.Once);
            Assert.Equal(1, aggregator.Messages.Count);
            Assert.Equal(message, aggregator.Messages.First());
            timer.Dispose();
        }

        [Fact]
        public void TimerShouldRemoveAllProcessedMessagesFromPersistor()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorTimer(mockPersistor.Object, mockContainer.Object, typeof(FakeAggregator));

            mockPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(1);
            var aggregator = new FakeAggregator();
            var message = new FakeMessage1(Guid.NewGuid());
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object> { message });

            // Act
            timer.StartTimer<FakeMessage1>(new TimeSpan(0, 0, 0, 0, 50));
            Thread.Sleep(90);

            // Assert 
            mockPersistor.Verify(x => x.RemoveAll(typeof(FakeMessage1).AssemblyQualifiedName), Times.Once);
           
            timer.Dispose();
        }

        [Fact]
        public void TimerShouldReset()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorTimer(mockPersistor.Object, mockContainer.Object, typeof(FakeAggregator));

            var count = 0;

            mockPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(0).Callback(() => count++);

            // Act
            timer.StartTimer<FakeMessage1>(new TimeSpan(0, 0, 0, 2));
            Thread.Sleep(1100);
            timer.ResetTimer();
            Thread.Sleep(1000);
            timer.Dispose();

            // Assert 
            Assert.Equal(0, count);
        }
    }
}