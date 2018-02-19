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
    public class FilterTests
    {
        private Mock<IConfiguration> _mockConfiguration;
        private Mock<IBusContainer> _mockContainer;
        private Mock<IConsumer> _mockConsumer;
        private Mock<IProducer> _mockProducer;
        private ConsumerEventHandler _fakeEventHandler;
        private List<HandlerReference> _handlerReferences;

        private static bool _beforeFilter1Ran;
        private static bool _beforeFilter2Ran;
        private static bool _afterFilter1Ran;
        private static bool _afterFilter2Ran;

        public class BeforeFilter1 : IFilter
        {
            public IBus Bus { get; set; }

            public bool Process(Envelope envelope)
            {
                var json = Encoding.UTF8.GetString(envelope.Body);
                var message = JsonConvert.DeserializeObject<FakeMessage1>(json);
                message.Username = "mutated";
                envelope.Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
                _beforeFilter1Ran = true;
                return true;
            }
        }

        public class BeforeFilter2 : IFilter
        {
            public IBus Bus { get; set; }

            public bool Process(Envelope envelope)
            {
                if (Bus == null)
                {
                    throw new Exception("Bus is null");
                }

                _beforeFilter2Ran = true;
                return true;
            }
        }

        public class BeforeFilter3 : IFilter
        {
            public IBus Bus { get; set; }

            public bool Process(Envelope envelope)
            {
                if (Bus == null)
                {
                    throw new Exception("Bus is null");
                }

                return false;
            }
        }

        public class AfterFilter1 : IFilter
        {
            public IBus Bus { get; set; }

            public bool Process(Envelope envelope)
            {
                if (Bus == null)
                {
                    throw new Exception("Bus is null");
                }

                _afterFilter1Ran = true;
                return true;
            }
        }

        public class AfterFilter2 : IFilter
        {
            public IBus Bus { get; set; }

            public bool Process(Envelope envelope)
            {
                if (Bus == null)
                {
                    throw new Exception("Bus is null");
                }

                _afterFilter2Ran = true;
                return true;
            }
        }

        public class AfterFilter3 : IFilter
        {
            public IBus Bus { get; set; }

            public bool Process(Envelope envelope)
            {
                if (Bus == null)
                {
                    throw new Exception("Bus is null");
                }

                return false;
            }
        }

        public FilterTests()
        {
            _beforeFilter1Ran = false;
            _beforeFilter2Ran = false;
            _afterFilter1Ran = false;
            _afterFilter2Ran = false;

            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { QueueName = "ServiceConnect.UnitTests" });
            _mockConfiguration.Setup(x => x.Threads).Returns(1);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);

            _handlerReferences = new List<HandlerReference>
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
        }

        [Fact]
        public void ShouldExecuteBeforeConsumingFilters()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var headers = new Dictionary<string, object>
            {
                { "MessageType", Encoding.ASCII.GetBytes("Send") }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(_handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            _mockContainer.Setup(x => x.GetInstance(typeof(BeforeFilter1))).Returns(new BeforeFilter1());
            _mockContainer.Setup(x => x.GetInstance(typeof(BeforeFilter2))).Returns(new BeforeFilter2());

            _mockConfiguration.Setup(x => x.BeforeConsumingFilters).Returns(new List<Type>
            {
                typeof (BeforeFilter1),
                typeof (BeforeFilter2)
            });

            bus.StartConsuming();

            var message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }));

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

            // Assert
            Assert.True(_beforeFilter1Ran);
            Assert.True(_beforeFilter2Ran);
        }

        [Fact]
        public void ShouldExecuteAfterConsumingFilters()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var headers = new Dictionary<string, object>
            {
                { "MessageType", Encoding.ASCII.GetBytes("Send") }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(_handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            _mockContainer.Setup(x => x.GetInstance(typeof(AfterFilter1))).Returns(new AfterFilter1());
            _mockContainer.Setup(x => x.GetInstance(typeof(AfterFilter2))).Returns(new AfterFilter2());

            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers))).Returns(Task.CompletedTask);
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers))).Returns(Task.CompletedTask);

            _mockConfiguration.Setup(x => x.AfterConsumingFilters).Returns(new List<Type>
            {
                typeof (AfterFilter1),
                typeof (AfterFilter2)
            });

            bus.StartConsuming();

            var message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }));

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers).GetAwaiter().GetResult();

            // Assert
            Assert.True(_afterFilter1Ran);
            Assert.True(_afterFilter2Ran);
        }

        [Fact]
        public void ShouldNotProcessMessageIfBeforeFilterReturnsFalse()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var headers = new Dictionary<string, object>
            {
                { "MessageType", Encoding.ASCII.GetBytes("Send") }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(_handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));

            _mockConfiguration.Setup(x => x.BeforeConsumingFilters).Returns(new List<Type>
            {
                typeof (BeforeFilter1),
                typeof (BeforeFilter2),
                typeof (BeforeFilter3)
            });

            bus.StartConsuming();

            var message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }));

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers).GetAwaiter().GetResult();

            // Assert
            mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.IsAny<IConsumeContext>()), Times.Never);
        }

        [Fact]
        public void ShouldProcessMessageIfBeforeFilterReturnsTrue()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var headers = new Dictionary<string, object>
            {
                { "MessageType", Encoding.ASCII.GetBytes("Send") }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(_handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            _mockContainer.Setup(x => x.GetInstance(typeof(BeforeFilter1))).Returns(new BeforeFilter1());
            _mockContainer.Setup(x => x.GetInstance(typeof(BeforeFilter2))).Returns(new BeforeFilter2());

            _mockConfiguration.Setup(x => x.BeforeConsumingFilters).Returns(new List<Type>
            {
                typeof (BeforeFilter1),
                typeof (BeforeFilter2)
            });

            bus.StartConsuming();

            var message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }));

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

            // Assert
            mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.IsAny<IConsumeContext>()), Times.Once);
        }

        [Fact]
        public void ShouldMutateMessage()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var headers = new Dictionary<string, object>
            {
                { "MessageType", Encoding.ASCII.GetBytes("Send") }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(_handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(It.IsAny<string>(), It.Is<IConsumeContext>(y => y.Headers == headers)));
            _mockContainer.Setup(x => x.GetInstance(typeof(BeforeFilter1))).Returns(new BeforeFilter1());

            _mockConfiguration.Setup(x => x.BeforeConsumingFilters).Returns(new List<Type>
            {
                typeof (BeforeFilter1)
            });

            bus.StartConsuming();

            var message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }));

            // Act
            _fakeEventHandler(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

            // Assert
            mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.Is<string>(j => JsonConvert.DeserializeObject<FakeMessage1>(j).Username == "mutated"), It.IsAny<IConsumeContext>()), Times.Once);
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }
    }
}