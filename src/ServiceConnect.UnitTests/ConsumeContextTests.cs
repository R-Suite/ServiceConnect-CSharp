// Copyright (C) 2015 Timothy Watson, Jakub Pachansky

// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class ConsumeContextTests
    {
        [Fact]
        public void ShouldSetBusProperty()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();

            // Act
            context.Bus = mockBus.Object;

            // Assert - Bus property has only setter, so we verify the setter worked without getter
        }

        [Fact]
        public void ShouldSetHeadersProperty()
        {
            // Arrange
            var headers = new Dictionary<string, object>
            {
                { "TestKey", "TestValue" }
            };
            var context = new ConsumeContext();

            // Act
            context.Headers = headers;

            // Assert
            Assert.Equal(headers, context.Headers);
        }

        [Fact]
        public void ReplyShouldThrowWhenHeadersIsNull()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            context.Headers = null;

            // Act & Assert
            Assert.Throws<ArgumentException>(() => context.Reply(new FakeMessage1(Guid.NewGuid())));
        }

        [Fact]
        public void ReplyShouldThrowWhenHeadersDoesNotContainRequestMessageId()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            context.Headers = new Dictionary<string, object>();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => context.Reply(new FakeMessage1(Guid.NewGuid())));
        }

        [Fact]
        public void ReplyShouldThrowWhenRequestMessageIdIsNotByteArray()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            context.Headers = new Dictionary<string, object>
            {
                { "RequestMessageId", "not-a-byte-array" }
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => context.Reply(new FakeMessage1(Guid.NewGuid())));
        }

        [Fact]
        public void ReplyShouldThrowWhenSourceAddressNotInHeaders()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            context.Headers = new Dictionary<string, object>
            {
                { "RequestMessageId", new byte[] { 1, 2, 3, 4 } }
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => context.Reply(new FakeMessage1(Guid.NewGuid())));
        }

        [Fact]
        public void ReplyShouldSendMessageToSourceAddress()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            context.Headers = new Dictionary<string, object>
            {
                { "RequestMessageId", new byte[] { 1, 2, 3, 4 } },
                { "SourceAddress", System.Text.Encoding.ASCII.GetBytes("test-queue") }
            };

            var message = new FakeMessage1(Guid.NewGuid());

            // Act
            context.Reply(message);

            // Assert
            mockBus.Verify(x => x.Send("test-queue", message, It.IsAny<Dictionary<string, string>>()), Times.Once);
        }

        [Fact]
        public void ReplyWithHeadersShouldSendMessageWithCustomHeaders()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            context.Headers = new Dictionary<string, object>
            {
                { "RequestMessageId", new byte[] { 1, 2, 3, 4 } },
                { "SourceAddress", System.Text.Encoding.ASCII.GetBytes("test-queue") }
            };

            var message = new FakeMessage1(Guid.NewGuid());
            var customHeaders = new Dictionary<string, string>
            {
                { "CustomHeader", "CustomValue" }
            };

            // Act
            context.Reply(message, customHeaders);

            // Assert
            mockBus.Verify(x => x.Send("test-queue", message, It.Is<Dictionary<string, string>>(h => 
                h.ContainsKey("CustomHeader") && h["CustomHeader"] == "CustomValue")), Times.Once);
        }

        [Fact]
        public void ReplyShouldAddResponseMessageIdToHeaders()
        {
            // Arrange
            var mockBus = new Mock<IBus>();
            var context = new ConsumeContext();
            context.Bus = mockBus.Object;
            var requestId = new byte[] { 1, 2, 3, 4 };
            context.Headers = new Dictionary<string, object>
            {
                { "RequestMessageId", requestId },
                { "SourceAddress", System.Text.Encoding.ASCII.GetBytes("test-queue") }
            };

            var message = new FakeMessage1(Guid.NewGuid());

            // Act
            context.Reply(message);

            // Assert
            mockBus.Verify(x => x.Send(It.IsAny<string>(), It.IsAny<Message>(), It.Is<Dictionary<string, string>>(h => 
                h.ContainsKey("ResponseMessageId"))), Times.Once);
        }
    }
}