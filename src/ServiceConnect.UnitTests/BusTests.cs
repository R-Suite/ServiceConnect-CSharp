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
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Fakes.Handlers;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class BusTests
    {
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;
        private readonly Mock<ISendMessagePipeline> _mockSendMessagePipeline;
        private ConsumerEventHandler _fakeEventHandler;
        private Guid _correlationId;
        private Mock<IProcessMessagePipeline> _mockProcessMessagePipeline;

        public BusTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { QueueName = "ServiceConnect.UnitTests" });
            _mockConfiguration.Setup(x => x.Clients).Returns(1);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
            _mockProcessMessagePipeline = new Mock<IProcessMessagePipeline>();
            _mockConfiguration.Setup(x => x.GetProcessMessagePipeline(It.IsAny<IBusState>())).Returns(_mockProcessMessagePipeline.Object);
            _mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(_mockSendMessagePipeline.Object);
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void DisposingBusShouldReturnFalseForIsConnected()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);
            _mockConsumer.Setup(x => x.IsConnected()).Returns(true);

            // Act
            bus.Dispose();

            // Assert
            Assert.False(bus.IsConnected());
        }

        [Fact]
        public void StartConsumingShouldReturnTrueForIsConnected()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);
            _mockConsumer.Setup(x => x.IsConnected()).Returns(true);

            // Act
            bus.StartConsuming();

            // Assert
            Assert.True(bus.IsConnected());
        }

        [Fact]
        public void StartConsumingShouldGetAllHandlerTypesFromContainer()
        {
            // Arrange
            var bus = new ServiceConnect.Bus(_mockConfiguration.Object);
            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(new List<HandlerReference>());

            // Act
            bus.StartConsuming();

            // Assert
            // One time for handlers and one time for aggregators
            _mockContainer.Verify(x => x.GetHandlerTypes(), Times.Exactly(2));
            _mockContainer.VerifyAll();
        }

        [Fact]
        public void StartConsumingShouldConsumeAllMessageTypes()
        {
            // Arrange
            var bus = new ServiceConnect.Bus(_mockConfiguration.Object);

            var handlerReferences = new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler1),
                    MessageType = typeof (FakeMessage1)
                },
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);

            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.Is<IList<string>>(m => m.Contains(typeof(FakeMessage1).FullName.Replace(".", string.Empty)) && m.Contains(typeof(FakeMessage2).FullName.Replace(".", string.Empty))), It.IsAny<ConsumerEventHandler>(), It.IsAny<IConfiguration>()));

            // Act
            bus.StartConsuming();

            // Assert
            _mockContainer.VerifyAll();
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<string>(), It.Is<IList<string>>(m => m.Contains(typeof(FakeMessage1).FullName.Replace(".", string.Empty)) && m.Contains(typeof(FakeMessage2).FullName.Replace(".", string.Empty))), It.IsAny<ConsumerEventHandler>(), It.IsAny<IConfiguration>()), Times.Once);
        }
        
        [Fact]
        public void ConsumeMessageEventShouldExecuteMessageProcessingPipeline()
        {
            // Arrange
            var bus = new ServiceConnect.Bus(_mockConfiguration.Object);

            var handlerReferences = new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler1),
                    MessageType = typeof (FakeMessage1)
                },
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                }
            };

            var headers = new Dictionary<string, object>
            {
                { "MessageType", Encoding.ASCII.GetBytes("Send") }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            
            bus.StartConsuming();

            var message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }));

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

            // Assert
            _mockProcessMessagePipeline.Verify(x => x.ExecutePipeline(It.Is<IConsumeContext>(y => y.Headers == headers), It.IsAny<Type>(), It.Is<Envelope>(y => ((FakeMessage1)JsonConvert.DeserializeObject(Encoding.UTF8.GetString(y.Body), typeof(FakeMessage1))).Username == "Tim Watson")), Times.Once);
        }
       
        private bool SetCorrelationId(Guid id)
        {
            _correlationId = id;
            return true;
        }

        [Fact]
        public void PublishShouldPublishMessage()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };


            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Publish(message, null);

            // Assert
            mockSendMessagePipeline.Setup(x => x.ExecutePublishMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()));
        }

        [Fact]
        public void PublishWithRoutingKeyShouldPublishMessage()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "JP"
            };

            mockProducer.Setup(x => x.Publish(typeof(FakeMessage1), It.IsAny<byte[]>(), null));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Publish(message, "routingkey1");

            // Assert

            mockSendMessagePipeline.Setup(x => x.ExecutePublishMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(i => i.ContainsKey("RoutingKey")), It.IsAny<string>()));
        }

        [Fact]
        public void SendShouldGetProducerFromContainer()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Send(new FakeMessage1(Guid.NewGuid()), null);

            // Assert
            mockConfiguration.Verify(x => x.GetProducer(), Times.Once());
        }

        [Fact]
        public void SendWithEndPointShouldGetProducerFromContainer()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Send("EndPoint", new FakeMessage1(Guid.NewGuid()), null);

            // Assert
            mockConfiguration.Verify(x => x.GetProducer(), Times.Once()); 
        }

        [Fact]
        public void SendShouldSendCommand()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(typeof(FakeMessage1), It.IsAny<byte[]>(), null));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Send(message, null);

            // Assert

            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public void SendShouldSendCommandUsingSpecifiedEndpoint()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            const string endPoint = "MyEndPoint";

            mockProducer.Setup(x => x.Send(endPoint, typeof(FakeMessage1), It.IsAny<byte[]>(), null));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Send(endPoint, message, null);

            // Assert

            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), endPoint), Times.Once);
        }

        [Fact]
        public void SendShouldSendCommandUsingSpecifiedEndpoints()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            List<string> endPoints = new List<string> { "MyEndPoint1", "MyEndPoint2" };

            foreach (string endPoint in endPoints)
            {
                mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), endPoint));
            }

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Send(endPoints, message, null);

            // Assert
            foreach (string endPoint in endPoints)
            {
                mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), endPoint), Times.Once);
            }
        }

        [Fact]
        public void SendingRequestSynchronouslyShouldSendCommand()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);
            
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>())).Callback(task.Start);

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>(message, null, 1000);

            // Assert

            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public void SendingRequestSynchronouslyShouldReturnResponse()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            Action<FakeMessage2> action = null;
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task)
                                                                                               .Callback<Action<FakeMessage2>>(r => action = r);
            
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>())).Callback(() =>
            {
                action(new FakeMessage2(message.CorrelationId)
                {
                    DisplayName = "Tim Watson",
                    Email = "twatson@test.com"
                });
                task.Start();
            });

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>(message, null, 1000);

            // Assert
            Assert.Equal("Tim Watson", response.DisplayName);
            Assert.Equal("twatson@test.com", response.Email);
            Assert.Equal(message.CorrelationId, response.CorrelationId);
        }

        [Fact]
        public void SendingRequestWithEndpointSynchronouslyShouldSendMessageToTheSpecifiedEndPoint()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test")).Callback(task.Start);

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>("test", message, null, 1000);

            // Assert
            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test"), Times.Once);
        }

        [Fact]
        public void SendingRequestWithEndpointSynchronouslyShouldReturnResponse()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            Action<FakeMessage2> action = null;
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task)
                                                                                               .Callback<Action<FakeMessage2>>(r => action = r);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };


            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test")).Callback(() =>
            {
                action(new FakeMessage2(message.CorrelationId)
                {
                    DisplayName = "Tim Watson",
                    Email = "twatson@test.com"
                });
                task.Start();
            });

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>("test", message, null, 1000);

            // Assert
            Assert.Equal("Tim Watson", response.DisplayName);
            Assert.Equal("twatson@test.com", response.Email);
            Assert.Equal(message.CorrelationId, response.CorrelationId);
        }

        [Fact]
        public void SendingRequestWithCallbackShouldSendCommand()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.SendRequest<FakeMessage1, FakeMessage2>(message, x => { }, null);

            // Assert

            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), null), Times.Once);

        }

        [Fact]
        public void SendingRequestWithCallbackShouldPassCallbackToHandler()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            bool actionCalled = false;
            Action<FakeMessage2> action = message2 => { actionCalled = true; };

            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Callback<Action<object>>(a => a(new FakeMessage2(Guid.NewGuid()))).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.SendRequest(message, action, null);

            // Assert
            mockRequestConfiguration.Verify(x => x.SetHandler(It.IsAny<Action<object>>()), Times.Once());
            Assert.True(actionCalled);
        }

        [Fact]
        public void SendingRequestWithEndpointAndCallbackShouldSendMessageToTheSpecifiedEndPoint()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };


            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.SendRequest<FakeMessage1, FakeMessage2>("test", message, x => { }, null);

            // Assert
            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test"), Times.Once);
        }

        [Fact]
        public void SendingRequestWithEndpointAndCallbackShouldPassCallbackToHandler()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            bool actionCalled = false;
            Action<FakeMessage2> action = message2 => { actionCalled = true; };

            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Callback<Action<object>>(a => a(new FakeMessage2(Guid.NewGuid()))).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.SendRequest("test", message, action, null);

            // Assert
            mockRequestConfiguration.Verify(x => x.SetHandler(It.IsAny<Action<object>>()), Times.Once());
            Assert.True(actionCalled);
        }

        [Fact]
        public void SendingRequestToMultipleEndpointsShouldPassResponsesToCallbackHandler()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            int count = 0;
            var r1 = new FakeMessage2(Guid.NewGuid());
            var r2 = new FakeMessage2(Guid.NewGuid());

            var responses = new List<FakeMessage2>();
            Action<FakeMessage2> action = message2 =>
            {
                count++;
                responses.Add(message2);
            };

            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Callback<Action<object>>(a =>
            {
                a(r1);
                a(r2);
            }).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.SendRequest(message, action, null);

            // Assert
            mockRequestConfiguration.Verify(x => x.SetHandler(It.IsAny<Action<object>>()), Times.Exactly(1));
            Assert.Equal(2, count);
            Assert.True(responses.Contains(r1));
            Assert.True(responses.Contains(r2));
        }

        [Fact]
        public void SendingRequestToMultipleEndpointsWithCallbackShouldSendMessageToSpecifiedEndpoints()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };


            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.SendRequest<FakeMessage1, FakeMessage2>(new List<string> { "test1", "test2" }, message, x => { });

            // Assert

            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test1"), Times.Once);
            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test2"), Times.Once);
        }

        [Fact]
        public void SendingRequestToMultipleEndpointsSynchronouslyShouldReturnResponses()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            Action<FakeMessage2> action = null;
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task).Callback<Action<FakeMessage2>>(r =>
            {
                action = r;
            });

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            var r1 = new FakeMessage2(Guid.NewGuid());
            var r2 = new FakeMessage2(Guid.NewGuid());


            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test1")).Callback(() =>
            {
                action(r1);
            });

            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test2")).Callback(() =>
            {
                action(r2);
                task.Start();
            });

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            IList<FakeMessage2> responses = bus.SendRequest<FakeMessage1, FakeMessage2>(new List<string>{ "test1", "test2" }, message, null, 1000);

            // Assert
            Assert.Equal(2, responses.Count);
            Assert.True(responses.Contains(r1));
            Assert.True(responses.Contains(r2));
        }

        [Fact]
        public void SendingRequestToMultipleEndpointsSynchronouslyShouldSendCommandsToSpecifiedEndpoints()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test2")).Callback(task.Start);


            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            var response = bus.SendRequest<FakeMessage1, FakeMessage2>(new List<string>
            {
                "test1",
                "test2"
            }, message, null, 1000);

            // Assert
            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test1"), Times.Once);
            mockSendMessagePipeline.Verify(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "test2"), Times.Once);
        }

        [Fact]
        public void PublishRequestShouldPublishMessagesAndReturnResponses()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();
            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<Guid>())).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            Action<FakeMessage2> action = null;
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task).Callback<Action<FakeMessage2>>(r =>
            {
                action = r;
            });

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            var r1 = new FakeMessage2(Guid.NewGuid());
            var r2 = new FakeMessage2(Guid.NewGuid());

            mockSendMessagePipeline.Setup(x => x.ExecutePublishMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>())).Callback(() =>
            {
                action(r1);
                action(r2);
                task.Start();
            });
            
            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            var responses = bus.PublishRequest<FakeMessage1, FakeMessage2>(message, 1, null, 1000);
            
            // Assert
            Assert.Equal(2, responses.Count);
            Assert.True(responses.Contains(r1));
            Assert.True(responses.Contains(r2));
        }

        [Fact]
        public void CustomExceptionHandlerShouldBeCalledIfConsumeMessageEventThrows()
        {
            // Arrange
            bool actionCalled = false;
            Action<Exception> action = exception => { actionCalled = true; };
            _mockConfiguration.Setup(x => x.ExceptionHandler).Returns(action);

            var bus = new ServiceConnect.Bus(_mockConfiguration.Object);

            var handlerReferences = new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler1),
                    MessageType = typeof (FakeMessage1)
                },
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                }
            };

            var headers = new Dictionary<string, object>();

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);

            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));

            _mockProcessMessagePipeline.Setup(x => x.ExecutePipeline(It.IsAny<IConsumeContext>(), It.IsAny<Type>(), It.IsAny<Envelope>())).Throws(new Exception());

            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })), typeof(FakeMessage1).FullName, headers);

            // Assert
            Assert.True(actionCalled);
        }

        [Fact]
        public void RouteShouldSendCommandWithRoutingSlipHeader()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();
            var mockSendMessagePipeline = new Mock<ISendMessagePipeline>();

            mockConfiguration.Setup(x => x.GetSendMessagePipeline()).Returns(mockSendMessagePipeline.Object);
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings());

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Jakub Pachansky"
            };

            const string endPoint1 = "MyEndPoint1";
            const string endPoint2 = "MyEndPoint2";

            mockProducer.Setup(x => x.Send(endPoint1, It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new ServiceConnect.Bus(mockConfiguration.Object);
            bus.Route(message, new List<string> { endPoint1, endPoint2 });

            // Assert
            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(i => i.Count == 1), It.IsAny<string>()));
            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(i => i.ContainsKey("RoutingSlip")), It.IsAny<string>()));
            mockSendMessagePipeline.Setup(x => x.ExecuteSendMessagePipeline(It.IsAny<Type>(), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(i => i.ContainsValue("[\"MyEndPoint2\"]")), It.IsAny<string>()));

        }
    }
}