using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Moq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using Xunit;

namespace R.MessageBus.UnitTests.Stream
{
    public class WriteStreamTests
    {
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int memcmp(byte[] b1, byte[] b2, long count);

        private readonly Mock<IProducer> _producer;

        public WriteStreamTests()
        {
            _producer = new Mock<IProducer>();
        }

        [Fact]
        public void WriteShouldSplitByteArrayIntoPacketsAndSendToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence");
            
            var byteArray = new byte[20];
            for (int i = 0; i < byteArray.Length; i++)
            {
                byteArray[i] = Convert.ToByte(i);
            }

            var packet1 = new byte[10];
            for (int i = 0; i < packet1.Length; i++)
            {
                packet1[i] = Convert.ToByte(i);
            }

            var packet2 = new byte[10];
            for (int i = 0; i < packet2.Length; i++)
            {
                packet2[i] = Convert.ToByte(i + 10);
            }

            // Act
            stream.Write(byteArray);

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => CompareByteArrays(y, packet1)), It.IsAny<Dictionary<string, string>>()));
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => CompareByteArrays(y, packet2)), It.IsAny<Dictionary<string, string>>()));
        }

        public bool CompareByteArrays(byte[] b1, byte[] b2)
        {
            return b1.Length == b2.Length && memcmp(b1, b2, b1.Length) == 0;
        }

        [Fact]
        public void WriteShouldSendTheSameSequenceNumberWithEachPacket()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence");

            var byteArray = new byte[20];

            // Act
            stream.Write(byteArray);

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["SequenceId"] == "TestSequence")));
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["SequenceId"] == "TestSequence")));
        }

        [Fact]
        public void WriteShouldIncrementPacketsSentNumberWhenSendingPacketToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence");

            var byteArray = new byte[20];

            // Act
            stream.Write(byteArray);

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["PacketNumber"] == "1")));
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["PacketNumber"] == "2")));
        }

        [Fact]
        public void CloseShouldSendAStopMessageToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence");

            // Act
            stream.Close();

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => y.Length == 0), It.Is<Dictionary<string, string>>(y => y.ContainsKey("Stop"))));
        }

        [Fact]
        public void DisposeShouldSendAStopMessageToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence");

            // Act
            stream.Dispose();

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => y.Length == 0), It.Is<Dictionary<string, string>>(y => y.ContainsKey("Stop"))));
        } 
    }
}