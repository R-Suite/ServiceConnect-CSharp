//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Moq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Aggregator
{
    public class AggregatorTimerTests
    {
        [Fact]
        public void ShouldStartAggregatorTimerIfAggregatorTimeoutIsSet()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();
            var mockAggregatorProcessor = new Mock<IAggregatorProcessor>();
            var mockConfiguration = new Mock<IConfiguration>();
            var mockConsumer = new Mock<IConsumer>();

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
            mockConfiguration.Setup(x => x.GetAggregatorProcessor(It.IsAny<IAggregatorPersistor>(), mockContainer.Object, typeof(FakeAggregator))).Returns(mockAggregatorProcessor.Object);
            mockConfiguration.Setup(x => x.GetAggregatorPersistor()).Returns(mockAggregatorPersistor.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.AutoStartConsuming).Returns(true);
            mockConfiguration.Setup(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetConsumer()).Returns(mockConsumer.Object);

            // Act
            new Bus(mockConfiguration.Object);

            // Assert
            mockAggregatorProcessor.Verify(x => x.StartTimer<FakeMessage1>(timeout), Times.Once);
        }

        [Fact(Skip = "Need to rething this test. Should not rely on thread.sleep")]
        public void TimerShouldRunEverySecond()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorProcessor(mockPersistor.Object, mockContainer.Object, typeof (FakeAggregator));

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

        [Fact(Skip = "Need to rething this test. Should not rely on thread.sleep")]
        public void TimerShouldGetMessagesFromAggregatorAndExecuteHandler()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorProcessor(mockPersistor.Object, mockContainer.Object, typeof(FakeAggregator));

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

        [Fact(Skip = "Need to rething this test. Should not rely on thread.sleep")]
        public void TimerShouldRemoveAllProcessedMessagesFromPersistor()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorProcessor(mockPersistor.Object, mockContainer.Object, typeof(FakeAggregator));

            mockPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(1);
            var aggregator = new FakeAggregator();
            var message = new FakeMessage1(Guid.NewGuid());
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object> { message });

            // Act
            timer.StartTimer<FakeMessage1>(new TimeSpan(0, 0, 0, 0, 50));
            Thread.Sleep(100);

            // Assert 
            mockPersistor.Verify(x => x.RemoveData(typeof(FakeMessage1).AssemblyQualifiedName, message.CorrelationId), Times.Once);
           
            timer.Dispose();
        }

        [Fact(Skip = "Need to rething this test. Should not rely on thread.sleep")]
        public void TimerShouldReset()
        {
            // Arrange
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockContainer = new Mock<IBusContainer>();

            var timer = new AggregatorProcessor(mockPersistor.Object, mockContainer.Object, typeof(FakeAggregator));

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