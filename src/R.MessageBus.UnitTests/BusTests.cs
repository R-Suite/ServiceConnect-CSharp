using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.Settings;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class BusTests
    {
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;
        private ConsumerEventHandler _fakeEventHandler;
        private Guid _correlationId;

        public BusTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
            _mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue { Name = "R.MessageBus.UnitTests" } });
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void StartConsumingShouldGetAllHandlerTypesFromContainer()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);
            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(new List<HandlerReference>());

            // Act
            bus.StartConsuming();

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(), Times.Once);
            _mockContainer.VerifyAll();
        }

        [Fact]
        public void StartConsumingShouldConsumeAllMessageTypes()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);
            _mockConsumer.Setup(x => x.ConsumeMessageType(It.IsAny<string>()));

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

            // Act
            bus.StartConsuming();

            // Assert
            _mockConfiguration.Verify(x => x.GetConsumer(), Times.Exactly(1));
            _mockContainer.VerifyAll();
            _mockConsumer.Verify(x => x.ConsumeMessageType(typeof(FakeMessage1).FullName.Replace(".", string.Empty)));
            _mockConsumer.Verify(x => x.ConsumeMessageType(typeof(FakeMessage2).FullName.Replace(".", string.Empty)));
        }

        [Fact]
        public void StartConsumingShouldStartConsumingAllMessagesFromTheContainer()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

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

            // Act
            bus.StartConsuming();

            // Assert
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "R.MessageBus.UnitTests", null, null), Times.Once);
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "R.MessageBus.UnitTests", null, null), Times.Once);
            _mockConsumer.VerifyAll();
        }

        [Fact]
        public void ConsumeMessageEventShouldProcessMessagesOnMessageHandler()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

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
            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), null, null));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));

            bus.StartConsuming();

            var message = JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            });

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

            // Assert
            mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.Is<string>(y => ((FakeMessage1)JsonConvert.DeserializeObject(y, typeof(FakeMessage1))).Username == "Tim Watson"), It.Is<IConsumeContext>(y => y.Headers == headers)), Times.Once);
        }

        [Fact]
        public void ConsumeMessageEventShouldProcessMessagesOnProcessManagers()
        {
            // Arrange
            var mockProcessManagerFinder = new Mock<IProcessManagerFinder>();
            _mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProcessManagerFinder.Object);

            var bus = new Bus(_mockConfiguration.Object);

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
            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), null, null));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object &&
                                                                                                                     y["processManagerFinder"] == mockProcessManagerFinder.Object))).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));

            bus.StartConsuming();
            var message = JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            });

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

            // Assert
            mockProcessManagerProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.Is<string>(y => ((FakeMessage1)JsonConvert.DeserializeObject(y, typeof(FakeMessage1))).Username == "Tim Watson"), It.Is<IConsumeContext>(y => y.Headers == headers)), Times.Once); 
        }


        [Fact]
        public void ConsumeMessageEventShouldProcessResponseMessage()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

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
            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), null, null));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage2>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage2>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));

            var mockRequestConfiguration = new Mock<IRequestConfiguration>();
            var mockProducer = new Mock<IProducer>();

            _mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.Is<Guid>(y => SetCorrelationId(y)), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var id = Guid.NewGuid();

            var message = new FakeMessage1(id)
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, It.IsAny<Dictionary<string, string>>()));

            _mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);

            bus.SendRequest<FakeMessage1, FakeMessage2>(message, x => { });
            bus.StartConsuming();

            headers["SourceAddress"] = Encoding.ASCII.GetBytes(_correlationId.ToString());

            var message2 = JsonConvert.SerializeObject(new FakeMessage2(id)
            {
            });


            // Act
            _fakeEventHandler(message2, typeof(FakeMessage2).AssemblyQualifiedName, headers);

            // Assert
            mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<FakeMessage2>(It.Is<string>(y => ((FakeMessage2)JsonConvert.DeserializeObject(y, typeof(FakeMessage2))).CorrelationId == id), It.Is<IConsumeContext>(y => y.Headers == headers)), Times.Once);
        }

        private bool SetCorrelationId(Guid id)
        {
            _correlationId = id;
            return true;
        }

        [Fact]
        public void PublishShouldGetProducerFromContainer()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            // Act
            var bus = new Bus(mockConfiguration.Object);
            bus.Publish(new FakeMessage1(Guid.NewGuid()));

            // Assert
            mockConfiguration.Verify(x => x.GetProducer(), Times.Once());
        }

        [Fact]
        public void PublishShouldPublishMessage()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Publish(message, null));

            // Act
            var bus = new MessageBus.Bus(mockConfiguration.Object);
            bus.Publish(message);

            // Assert
            mockProducer.Verify(x => x.Publish(message, null), Times.Once);
        }

        [Fact]
        public void SendShouldGetProducerFromContainer()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            // Act
            var bus = new MessageBus.Bus(mockConfiguration.Object);
            bus.Send(new FakeMessage1(Guid.NewGuid()));

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

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            // Act
            var bus = new MessageBus.Bus(mockConfiguration.Object);
            bus.Send("EndPoint", new FakeMessage1(Guid.NewGuid()));

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

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, null));

            // Act
            var bus = new MessageBus.Bus(mockConfiguration.Object);
            bus.Send(message);

            // Assert
            mockProducer.Verify(x => x.Send(message, null), Times.Once);
        }

        [Fact]
        public void SendShouldSendCommandUsingSpecifiedEndpoint()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockProessManagerFinder = new Mock<IProcessManagerFinder>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProessManagerFinder.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            const string endPoint = "MyEndPoint";

            mockProducer.Setup(x => x.Send(endPoint, message, null));

            // Act
            var bus = new MessageBus.Bus(mockConfiguration.Object);
            bus.Send(endPoint, message);

            // Assert
            mockProducer.Verify(x => x.Send(endPoint, message, null), Times.Once);
        }

        [Fact]
        public void SendingRequestSynchronouslyShouldSendCommand()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);
            
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, It.IsAny<Dictionary<string, string>>())).Callback(task.Start);

            // Act
            var bus = new Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>(message, 1000);

            // Assert
            mockProducer.Verify(x => x.Send(message, It.IsAny<Dictionary<string, string>>()), Times.Once);
        }

        [Fact]
        public void SendingRequestSynchronouslyShouldReturnResponse()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            Action<FakeMessage2> action = null;
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task)
                                                                                               .Callback<Action<FakeMessage2>>(r => action = r);
            
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, It.IsAny<Dictionary<string, string>>())).Callback(() =>
            {
                action(new FakeMessage2(message.CorrelationId)
                {
                    DisplayName = "Tim Watson",
                    Email = "twatson@test.com"
                });
                task.Start();
            });

            // Act
            var bus = new Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>(message, 1000);

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

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send("test", message, It.IsAny<Dictionary<string, string>>())).Callback(task.Start);

            // Act
            var bus = new Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>("test", message, 1000);

            // Assert
            mockProducer.Verify(x => x.Send("test", message, It.IsAny<Dictionary<string, string>>()), Times.Once);
        }

        [Fact]
        public void SendingRequestWithEndpointSynchronouslyShouldReturnResponse()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            Action<FakeMessage2> action = null;
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task)
                                                                                               .Callback<Action<FakeMessage2>>(r => action = r);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send("test", message, It.IsAny<Dictionary<string, string>>())).Callback(() =>
            {
                action(new FakeMessage2(message.CorrelationId)
                {
                    DisplayName = "Tim Watson",
                    Email = "twatson@test.com"
                });
                task.Start();
            });

            // Act
            var bus = new Bus(mockConfiguration.Object);
            FakeMessage2 response = bus.SendRequest<FakeMessage1, FakeMessage2>("test", message, 1000);

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

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new Bus(mockConfiguration.Object);
            bus.SendRequest<FakeMessage1, FakeMessage2>(message, x => {});

            // Assert
            mockProducer.Verify(x => x.Send(message, It.IsAny<Dictionary<string, string>>()), Times.Once);
        }

        [Fact]
        public void SendingRequestWithCallbackShouldPassCallbackToHandler()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            bool actionCalled = false;
            Action<FakeMessage2> action = message2 => { actionCalled = true; };

            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Callback<Action<object>>(a => a(new FakeMessage2(Guid.NewGuid()))).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new Bus(mockConfiguration.Object);
            bus.SendRequest(message, action);

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

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });
            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send("test", message, It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new Bus(mockConfiguration.Object);
            bus.SendRequest<FakeMessage1, FakeMessage2>("test", message, x => { });

            // Assert
            mockProducer.Verify(x => x.Send("test", message, It.IsAny<Dictionary<string, string>>()), Times.Once);
        }

        [Fact]
        public void SendingRequestWithEndpointAndCallbackShouldPassCallbackToHandler()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockProducer = new Mock<IProducer>();
            var mockContainer = new Mock<IBusContainer>();
            var mockRequestConfiguration = new Mock<IRequestConfiguration>();

            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);
            mockConfiguration.Setup(x => x.GetProducer()).Returns(mockProducer.Object);
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });
            mockConfiguration.Setup(x => x.GetRequestConfiguration(It.IsAny<ConsumerEventHandler>(), It.IsAny<Guid>(), typeof(FakeMessage2).FullName.Replace(".", string.Empty))).Returns(mockRequestConfiguration.Object);
            var task = new Task(() => { });

            bool actionCalled = false;
            Action<FakeMessage2> action = message2 => { actionCalled = true; };

            mockRequestConfiguration.Setup(x => x.SetHandler(It.IsAny<Action<object>>())).Callback<Action<object>>(a => a(new FakeMessage2(Guid.NewGuid()))).Returns(task);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };

            mockProducer.Setup(x => x.Send(message, It.IsAny<Dictionary<string, string>>()));

            // Act
            var bus = new Bus(mockConfiguration.Object);
            bus.SendRequest("test", message, action);

            // Assert
            mockRequestConfiguration.Verify(x => x.SetHandler(It.IsAny<Action<object>>()), Times.Once());
            Assert.True(actionCalled);
        }

        [Fact]
        public void CustomExceptionHandlerShouldBeCalledIfConsumeMessageEventThrows()
        {
            // Arrange
            bool actionCalled = false;
            Action<Exception> action = exception => { actionCalled = true; };
            _mockConfiguration.Setup(x => x.ExceptionHandler).Returns(action);

            var bus = new Bus(_mockConfiguration.Object);

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

            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), null, null));

            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers))).Throws(new Exception());

            bus.StartConsuming();

            // Act
            _fakeEventHandler(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }), typeof(FakeMessage2).FullName, headers);

            // Assert
            Assert.True(actionCalled);
        }

    }
}