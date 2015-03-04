using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using Newtonsoft.Json;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Aggregator
{
    public class FakeAggregator : Aggregator<FakeMessage1>
    {
        public TimeSpan Time { get; set; }
        public bool Executed { get; set; }
        public int Batch { get; set; }

        public override TimeSpan Timeout()
        {
            return Time;
        }

        public override int BatchSize()
        {
            return Batch;
        }

        public override void Execute(IList<FakeMessage1> message)
        {
            Messages = message;
            Executed = true;
        }

        public IList<FakeMessage1> Messages { get; set; }
    }

    public class AggregatorProcessorTests
    {
        [Fact]
        public void ShouldFindTheAggregatorForTheMessageType()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();
            
            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Core.Aggregator<FakeMessage1>))).Returns(new List<HandlerReference>{ handlerRef });
            mockContainer.Setup(x => x.GetInstance(typeof (FakeAggregator))).Returns(new FakeAggregator
            {
                Time = new TimeSpan(0, 0, 0, 1)
            });
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));


            var aggregator = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);
            
            // Act
            aggregator.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockContainer.Verify(x => x.GetHandlerTypes(typeof(Core.Aggregator<FakeMessage1>)), Times.Once);
        }

        [Fact]
        public void ShouldThrowExceptionIfThereAreMultipleAggregatorsForTheSameMessageType()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();
            
            var handlerRef = new HandlerReference
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference>{ handlerRef, handlerRef });
            mockContainer.Setup(x => x.GetInstance(typeof (FakeAggregator))).Returns(new FakeAggregator
            {
                Time = new TimeSpan(0, 0, 0, 1)
            });
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));
            
            var aggregator = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);
            
            // Act and Assert
            Assert.Throws<Exception>(() => aggregator.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid()))));
            
            mockAggregatorPersistor.Verify(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()), Times.Never);
        }
        

        [Fact]
        public void ShouldDoNothingIfThereAreNoAggregatorsForMessageType()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference>());
           
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            var aggregator = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act and Assert
            Assert.DoesNotThrow(() => aggregator.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid()))));

            mockAggregatorPersistor.Verify(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void IfTimerIsNotSetAndBatchSizeIsNotSetThenBatchSizeIsSetTo10()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var aggregator = new FakeAggregator();
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            mockAggregatorPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(10);
            mockAggregatorPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>());
            
            var aggregatorProcessor = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregatorProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            // Only calls this if batchsize is equal to number of messages in persistance store.
            mockAggregatorPersistor.Verify(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName), Times.Once);
        }

        [Fact]
        public void ShouldAddMessageToAggregatorPersistor()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Core.Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(new FakeAggregator
            {
                Time = new TimeSpan(0, 0, 0, 1)
            });
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));


            var aggregator = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregator.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockAggregatorPersistor.Verify(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void ShouldNotExecuteAggregatorIfBatchSizeHasntBeenSet()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Core.Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var fakeAggregator = new FakeAggregator
            {
                Time = new TimeSpan(0, 0, 0, 1)
            };
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(fakeAggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));


            var aggregator = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregator.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockAggregatorPersistor.Verify(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName), Times.Never);
            Assert.False(fakeAggregator.Executed);
        }

        [Fact]
        public void ShouldExecuteHandlerWithMessagesIfMessageCountIsEqualToOrGreaterThanBatchSize()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var aggregator = new FakeAggregator();
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            mockAggregatorPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(10);
            mockAggregatorPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>());

            var aggregatorProcessor = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregatorProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockAggregatorPersistor.Verify(x => x.RemoveAll(typeof(FakeMessage1).AssemblyQualifiedName), Times.Once);

        }

        [Fact]
        public void ShouldRemoveProcessedMessagesFromPersistor()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var aggregator = new FakeAggregator();
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            mockAggregatorPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(10);
            mockAggregatorPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>());

            var aggregatorProcessor = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregatorProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockAggregatorPersistor.Verify(x => x.RemoveAll(typeof(FakeMessage1).AssemblyQualifiedName), Times.Once);
        }

        [Fact]
        public void ShouldNotRemoveUnProcessedMessagesFromPersistor()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var aggregator = new FakeAggregator();
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            mockAggregatorPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(9);
            mockAggregatorPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>());

            var aggregatorProcessor = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregatorProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockAggregatorPersistor.Verify(x => x.RemoveAll(typeof(FakeMessage1).AssemblyQualifiedName), Times.Never);
        }

        [Fact]
        public void ShouldUseBatchSizeFromAggregator()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference()
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var aggregator = new FakeAggregator
            {
                Batch = 20
            };
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            mockAggregatorPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(20);
            mockAggregatorPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>());

            var aggregatorProcessor = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);

            // Act
            aggregatorProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            mockAggregatorPersistor.Verify(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName), Times.Once);

        }

        [Fact]
        public void ShouldRaiseBatchProcessedEventIfMessageCountIsEqualToOrGreaterThanBatchSize()
        {
            // Arrange
            var mockContainer = new Mock<IBusContainer>();
            var mockAggregatorPersistor = new Mock<IAggregatorPersistor>();

            var handlerRef = new HandlerReference
            {
                HandlerType = typeof(FakeAggregator),
                MessageType = typeof(FakeMessage1)
            };
            mockContainer.Setup(x => x.GetHandlerTypes(typeof(Aggregator<FakeMessage1>))).Returns(new List<HandlerReference> { handlerRef });
            var aggregator = new FakeAggregator();
            mockContainer.Setup(x => x.GetInstance(typeof(FakeAggregator))).Returns(aggregator);
            mockAggregatorPersistor.Setup(x => x.InsertData(It.IsAny<object>(), It.IsAny<string>()));

            mockAggregatorPersistor.Setup(x => x.Count(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(10);
            mockAggregatorPersistor.Setup(x => x.GetData(typeof(FakeMessage1).AssemblyQualifiedName)).Returns(new List<object>());

            var aggregatorProcessor = new AggregatorProcessor(mockAggregatorPersistor.Object, mockContainer.Object);
            bool eventRaised = false;
            aggregatorProcessor.BatchProcessed += (type, args) => { eventRaised = true; };

            // Act
            aggregatorProcessor.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(new FakeMessage1(Guid.NewGuid())));

            // Assert
            Assert.True(eventRaised);
        }
    }
}