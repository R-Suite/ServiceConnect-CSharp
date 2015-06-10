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
        private Mock<IConfiguration> _mockConfigurtaion;

        public WriteStreamTests()
        {
            _producer = new Mock<IProducer>();
            _mockConfigurtaion = new Mock<IConfiguration>();
        }

        [Fact]
        public void WriteShouldSplitByteArrayIntoPacketsAndSendToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence", _mockConfigurtaion.Object);
            
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
            stream.Write(byteArray, 0, byteArray.Length);

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => CompareByteArrays(y, packet1)), It.IsAny<Dictionary<string, string>>(), It.IsAny<IList<Type>>()));
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => CompareByteArrays(y, packet2)), It.IsAny<Dictionary<string, string>>(), It.IsAny<IList<Type>>()));
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
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence", _mockConfigurtaion.Object);

            var byteArray = new byte[20];

            // Act
            stream.Write(byteArray, 0, byteArray.Length);

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["SequenceId"] == "TestSequence"), It.IsAny<IList<Type>>()));
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["SequenceId"] == "TestSequence"), It.IsAny<IList<Type>>()));
        }

        [Fact]
        public void WriteShouldIncrementPacketsSentNumberWhenSendingPacketToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence", _mockConfigurtaion.Object);

            var byteArray = new byte[20];

            // Act
            stream.Write(byteArray, 0, byteArray.Length);

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["PacketNumber"] == "1"), It.IsAny<IList<Type>>()));
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.IsAny<byte[]>(), It.Is<Dictionary<string, string>>(y => y["PacketNumber"] == "2"), It.IsAny<IList<Type>>()));
        }

        [Fact]
        public void CloseShouldSendAStopMessageToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence", _mockConfigurtaion.Object);

            // Act
            stream.Close();

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => y.Length == 0), It.Is<Dictionary<string, string>>(y => y.ContainsKey("Stop")), It.IsAny<IList<Type>>()));
        }

        [Fact]
        public void DisposeShouldSendAStopMessageToEndpoint()
        {
            // Arrange
            _producer.Setup(x => x.MaximumMessageSize).Returns(10);
            var stream = new MessageBusWriteStream(_producer.Object, "TestEndpoint", "TestSequence", _mockConfigurtaion.Object);

            // Act
            stream.Dispose();

            // Assert
            _producer.Verify(x => x.SendBytes("TestEndpoint", It.Is<byte[]>(y => y.Length == 0), It.Is<Dictionary<string, string>>(y => y.ContainsKey("Stop")), It.IsAny<IList<Type>>()));
        } 
    }
}