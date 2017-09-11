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
using System.Text;
using Moq;
using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Aggregator;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class ConsumeMessageEventTests
    {
        private Mock<IConfiguration> _mockConfiguration;
        private Mock<IBusContainer> _mockContainer;
        private Mock<IConsumer> _mockConsumer;
        private Mock<IProducer> _mockProducer;
        private ConsumerEventHandler _fakeEventHandler;

        public ConsumeMessageEventTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockProducer = new Mock<IProducer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetProducer()).Returns(_mockProducer.Object);
            _mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { QueueName = "ServiceConnect.UnitTests" });
            
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void ShouldSendMessageToAggregatorProcessor()
        {
            // Arrange
            
            var bus = new ServiceConnect.Bus(_mockConfiguration.Object);

            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockProcessor = new Mock<IAggregatorProcessor>();
            var fakeAggregator = new FakeAggregator();

            _mockConfiguration.Setup(x => x.GetAggregatorPersistor()).Returns(mockPersistor.Object);
            _mockConfiguration.Setup(x => x.Threads).Returns(1);
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(new Mock<IMessageHandlerProcessor>().Object);
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(new Mock<IProcessManagerProcessor>().Object);
            _mockConfiguration.Setup(x => x.GetAggregatorProcessor(It.IsAny<IAggregatorPersistor>(), It.IsAny<IBusContainer>(), It.IsAny<Type>())).Returns(mockProcessor.Object);
            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeAggregator),
                    MessageType = typeof (FakeMessage1)
                }
            });
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(fakeAggregator);
            
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            mockProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message)));
                
            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "MessageType", Encoding.UTF8.GetBytes("Send")}
            });

            // Assert
            mockProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.Is<string>(y => JsonConvert.DeserializeObject<FakeMessage1>(y).Username == "Tim")), Times.Once);
        }

        [Fact]
        public void ShouldNotCreateMultipleConsumers()
        {
            // Arrange
            _mockConfiguration.SetupGet(x => x.AutoStartConsuming).Returns(true);
            _mockConfiguration.SetupGet(x => x.ScanForMesssageHandlers).Returns(false);
            _mockConfiguration.Setup(x => x.SetAuditingEnabled(false));
            _mockConfiguration.Setup(x => x.Threads).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>(), It.IsAny<IConfiguration>()));

            // Act
            var bus = new ServiceConnect.Bus(_mockConfiguration.Object);

            // Assert
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>(), It.IsAny<IConfiguration>()), Times.Once);
        }
    }
}