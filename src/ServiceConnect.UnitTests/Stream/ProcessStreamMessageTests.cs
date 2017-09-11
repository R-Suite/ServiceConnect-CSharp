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
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Stream
{
    public class ProcessStreamMessageTests
    {
        private Mock<IConfiguration> _mockConfiguration;
        private Mock<IBusContainer> _mockContainer;
        private Mock<IConsumer> _mockConsumer;
        private Mock<IProducer> _mockProducer;
        private ConsumerEventHandler _fakeEventHandler;

        public ProcessStreamMessageTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockProducer = new Mock<IProducer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetProducer()).Returns(_mockProducer.Object);
            _mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { QueueName = "ServiceConnect.UnitTests" });
            _mockConfiguration.Setup(x => x.Threads).Returns(1);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void StartMessageShouldCreateANewMessageBusReadStream()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var mockStream = new Mock<IMessageBusReadStream>();
            mockStream.Setup(x => x.HandlerCount).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockProcessor = new Mock<IStreamProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IStreamProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockProcessor.Object);
            mockProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), mockStream.Object));
            _mockConfiguration.Setup(x => x.GetMessageBusReadStream()).Returns(mockStream.Object);
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "Start", "" },
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")}
            });

            // Assert
            mockProcessor.Verify(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), It.IsAny<IMessageBusReadStream>()), Times.Once);
        }

        [Fact]
        public void StartMessageShouldCallProcessMessageOnStreamProcessor()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var mockStream = new Mock<IMessageBusReadStream>();
            mockStream.Setup(x => x.HandlerCount).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockProcessor = new Mock<IStreamProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IStreamProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockProcessor.Object);
            mockProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), mockStream.Object));
            _mockConfiguration.Setup(x => x.GetMessageBusReadStream()).Returns(mockStream.Object);
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "Start", "" },
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")}
            });

            // Assert
            mockProcessor.Verify(x => x.ProcessMessage(It.Is<FakeMessage1>(y => y.Username == "Tim"), It.IsAny<IMessageBusReadStream>()), Times.Once);
        }

        [Fact]
        public void AfterStartMessageHasBeenProcessedAResponseShouldBeSentBackToTheSource()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var mockStream = new Mock<IMessageBusReadStream>();
            mockStream.Setup(x => x.HandlerCount).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockProcessor = new Mock<IStreamProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IStreamProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockProcessor.Object);
            mockProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), mockStream.Object));
            _mockConfiguration.Setup(x => x.GetMessageBusReadStream()).Returns(mockStream.Object);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            _mockProducer.Setup(x => x.Send("Source", typeof(StreamResponseMessage), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["ResponseMessageId"] == "MessageId")));

            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "Start", "" },
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")}
            });

            // Assert
            _mockProducer.Verify(x => x.Send("Source", typeof(StreamResponseMessage), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["ResponseMessageId"] == "MessageId")), Times.Once);
        }

        [Fact]
        public void IfByteStreamHasntBeenStartedAndBusRecievesAStreamMessageBusShouldIgnoreIt()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var mockStream = new Mock<IMessageBusReadStream>();
            mockStream.Setup(x => x.HandlerCount).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockProcessor = new Mock<IStreamProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IStreamProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockProcessor.Object);
            mockProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), mockStream.Object));
            _mockConfiguration.Setup(x => x.GetMessageBusReadStream()).Returns(mockStream.Object);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };
            
            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")}
            });

            // Assert
            mockStream.Verify(x => x.Write(It.IsAny<byte[]>(), It.IsAny<long>()), Times.Never);
        }

        [Fact]
        public void ConsumeMessageEventShouldProcessStreamMessage()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var mockStream = new Mock<IMessageBusReadStream>();
            mockStream.Setup(x => x.HandlerCount).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockProcessor = new Mock<IStreamProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IStreamProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockProcessor.Object);
            mockProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), mockStream.Object));
            _mockConfiguration.Setup(x => x.GetMessageBusReadStream()).Returns(mockStream.Object);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            _mockProducer.Setup(x => x.Send("Source", typeof(StreamResponseMessage), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["ResponseMessageId"] == "MessageId")));

            bus.StartConsuming();

            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "Start", "" },
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")},
                 
            });

            var streamMessage = new byte[]{ 0,1,2,3,4,5,6,7,8,9 };

            mockStream.Setup(x => x.Write(It.Is<byte[]>(y => streamMessage == y), It.Is<long>(y => y == 1)));

            // Act
            _fakeEventHandler(streamMessage, typeof(byte[]).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")},
                { "PacketNumber", Encoding.UTF8.GetBytes("1")}
            });

            // Assert 
            mockStream.Verify(x => x.Write(It.Is<byte[]>(y => streamMessage == y), It.Is<long>(y => y == 1)), Times.Once);
        }

        [Fact]
        public void ConsumeMessageEventShouldStopStreamIfStopMessageIsRecieved()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var mockStream = new Mock<IMessageBusReadStream>();
            mockStream.Setup(x => x.HandlerCount).Returns(1);
            _mockConsumer.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));
            var mockProcessor = new Mock<IStreamProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IStreamProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockProcessor.Object);
            mockProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>(), mockStream.Object));
            _mockConfiguration.Setup(x => x.GetMessageBusReadStream()).Returns(mockStream.Object);

            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            _mockProducer.Setup(x => x.Send("Source", typeof(StreamResponseMessage), It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["ResponseMessageId"] == "MessageId")));

            bus.StartConsuming();

            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "Start", "" },
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")},
                 
            });

            var streamMessage = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

            // Act
            _fakeEventHandler(streamMessage, typeof(byte[]).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "SequenceId", Encoding.UTF8.GetBytes("TestSequence") },
                { "SourceAddress", Encoding.UTF8.GetBytes("Source") },
                { "RequestMessageId", Encoding.UTF8.GetBytes("MessageId") },
                { "MessageType", Encoding.UTF8.GetBytes("ByteStream")},
                { "PacketNumber", Encoding.UTF8.GetBytes("2")},
                { "Stop", "" }
            });

            // Assert 
            mockStream.Verify(x => x.Write(It.IsAny<byte[]>(), It.IsAny<long>()), Times.Never);
            mockStream.VerifySet(x => x.LastPacketNumber = 2, Times.Once);
        }
    }
}