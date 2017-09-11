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
using System.Text;
using Moq;
using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Fakes.Handlers;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class MessageHandlerProcessorTest
    {
        private readonly Mock<IBusContainer> _mockContainer;

        public MessageHandlerProcessorTest()
        {
            _mockContainer = new Mock<IBusContainer>();
        }

        [Fact]
        public void ProcessMessageShouldGetTheCorrectHandlerTypesFromContainer()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            // Act
            messageProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }), null).GetAwaiter().GetResult();

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(It.Is<Type[]>(y => y.Contains(typeof(IMessageHandler<FakeMessage1>)) && y.Contains(typeof(IAsyncMessageHandler<FakeMessage1>)))), Times.Once());
        }
       
        [Fact]
        public void ShouldExecuteTheCorrectHandlers()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof (FakeHandler1),
                MessageType = typeof (FakeMessage1)
            };

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>), typeof(IAsyncMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeHandler1();
            _mockContainer.Setup(x => x.GetInstance(typeof (FakeHandler1))).Returns(fakeHandler);

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };
            messageProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message1), null).GetAwaiter().GetResult(); ;

            var message2 = new FakeMessage2(Guid.NewGuid())
            {
                DisplayName = "Tim Watson"
            };

            messageProcessor.ProcessMessage<FakeMessage2>(JsonConvert.SerializeObject(message2), null).GetAwaiter().GetResult(); ;

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof (FakeHandler2)), Times.Never);
        }

        [Fact]
        public void ShouldExecuteTheCorrectBaseMessageHandlers()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof(FakeBaseMessageHandler1),
                MessageType = typeof(FakeBaseMessage1)
            };

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeBaseMessage1>), typeof(IAsyncMessageHandler<FakeBaseMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeBaseMessageHandler1();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeBaseMessageHandler1))).Returns(fakeHandler);

            // Act
            var message = new FakeDerivedMessage1(Guid.NewGuid())
            {
                Status = "Test"
            };

            messageProcessor.ProcessMessage<FakeDerivedMessage1>(JsonConvert.SerializeObject(message), null).GetAwaiter().GetResult(); ;

            // Assert
            Assert.Equal(message.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof(FakeBaseMessageHandler1)), Times.Once);
        }

        [Fact]
        public void ShouldExecuteTheCorrectHandlerWithRoutingKeyAttribute()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof(FakeHandlerWithAttr1),
                MessageType = typeof(FakeMessage1),
                RoutingKeys = new List<string> { "Test"}
            };

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>), typeof(IAsyncMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeHandlerWithAttr1();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeHandlerWithAttr1))).Returns(fakeHandler);

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Jakub Pachansky"
            };
            messageProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message1),
                new ConsumeContext {Headers = new Dictionary<string, object> {{"RoutingKey", Encoding.ASCII.GetBytes("Test") }}}).GetAwaiter().GetResult(); ;

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof(FakeHandlerWithAttr1)), Times.Once);
        }

        [Fact]
        public void ShouldExecuteTheCorrectHandlerWithCatchAllRoutingKeyAttribute()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof(FakeHandlerWithAttr1),
                MessageType = typeof(FakeMessage1),
                RoutingKeys = new List<string> { "#" } // matches any routing key
            };

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>), typeof(IAsyncMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeHandlerWithAttr1();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeHandlerWithAttr1))).Returns(fakeHandler);

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Jakub Pachansky"
            };
            messageProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message1),
                new ConsumeContext { Headers = new Dictionary<string, object> { { "RoutingKey", Encoding.ASCII.GetBytes("SomeRandomRoutingKey") } } }).GetAwaiter().GetResult(); ;

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof(FakeHandlerWithAttr1)), Times.Once);
        }

        [Fact]
        public void ShouldExecuteTheCorrectHandlerWithMultipleRoutingKeyAttributes()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof(FakeHandlerWithAttr2),
                MessageType = typeof(FakeMessage1),
                RoutingKeys = new List<string> { "Test1", "Test2" }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>), typeof(IAsyncMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeHandlerWithAttr2();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeHandlerWithAttr2))).Returns(fakeHandler);

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Jakub Pachansky"
            };
            messageProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message1),
                new ConsumeContext { Headers = new Dictionary<string, object> {{"RoutingKey", Encoding.ASCII.GetBytes("Test2")}}}).GetAwaiter().GetResult(); ;

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof(FakeHandlerWithAttr2)), Times.Once);
        }

        [Fact]
        public void ShouldExecuteAsyncHandler()
        {
            // Arrange
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof(FakeAsyncHandler),
                MessageType = typeof(FakeMessage1)
            };

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>), typeof(IAsyncMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeAsyncHandler();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeAsyncHandler))).Returns(fakeHandler);

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid());

            messageProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message1),
                new ConsumeContext { Headers = new Dictionary<string, object>() }).GetAwaiter().GetResult(); ;

            // Assert
            Assert.Equal(true, fakeHandler.Executed);
        }
    }
}