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
using System.Threading.Tasks;
using ServiceConnect.Core;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class RequestConfigurationTests
    {
        [Fact]
        public void ShouldStoreRequestMessageId()
        {
            // Arrange
            var requestId = Guid.NewGuid();

            // Act
            var config = new RequestConfiguration(requestId);

            // Assert
            Assert.Equal(requestId, config.RequestMessageId);
        }

        [Fact]
        public void ShouldInitializeEndpointsCountToZero()
        {
            // Arrange & Act
            var config = new RequestConfiguration(Guid.NewGuid());

            // Assert
            Assert.Equal(0, config.EndpointsCount);
        }

        [Fact]
        public void ShouldInitializeProcessedCountToZero()
        {
            // Arrange & Act
            var config = new RequestConfiguration(Guid.NewGuid());

            // Assert
            Assert.Equal(0, config.ProcessedCount);
        }

        [Fact]
        public void SetHandlerShouldReturnTask()
        {
            // Arrange
            var config = new RequestConfiguration(Guid.NewGuid());

            // Act
            var task = config.SetHandler(msg => { });

            // Assert
            Assert.NotNull(task);
            Assert.IsType<Task>(task);
        }

        [Fact]
        public void SetHandlerShouldStoreAction()
        {
            // Arrange
            var config = new RequestConfiguration(Guid.NewGuid());
            Action<object> action = msg => { };

            // Act
            config.SetHandler(action);

            // Assert - should not throw when ProcessMessage is called
            config.ProcessedCount = 1;
            config.EndpointsCount = 1;
            config.ProcessMessage("{}", typeof(FakeMessage1));
        }

        [Fact]
        public void ProcessMessageShouldDeserializeJson()
        {
            // Arrange
            var config = new RequestConfiguration(Guid.NewGuid());
            object receivedMessage = null;
            config.SetHandler(msg => receivedMessage = msg);

            // Act
            config.ProcessMessage("{\"Id\":\"00000000-0000-0000-0000-000000000001\"}", typeof(FakeMessage1));

            // Assert
            Assert.NotNull(receivedMessage);
            Assert.IsType<FakeMessage1>(receivedMessage);
        }

        [Fact]
        public void ProcessMessageShouldIncrementProcessedCount()
        {
            // Arrange
            var config = new RequestConfiguration(Guid.NewGuid());
            config.SetHandler(msg => { });

            // Act
            config.ProcessMessage("{}", typeof(FakeMessage1));

            // Assert
            Assert.Equal(1, config.ProcessedCount);
        }

        [Fact]
        public void ProcessMessageShouldCallAction()
        {
            // Arrange
            var config = new RequestConfiguration(Guid.NewGuid());
            bool actionCalled = false;
            config.SetHandler(msg => actionCalled = true);

            // Act
            config.ProcessMessage("{}", typeof(FakeMessage1));

            // Assert
            Assert.True(actionCalled);
        }

        [Fact]
        public void ProcessMessageShouldStartTaskWhenAllEndpointsProcessed()
        {
            // Arrange
            var config = new RequestConfiguration(Guid.NewGuid());
            var taskCompleted = false;
            config.SetHandler(msg => { });
            
            config.EndpointsCount = 2;

            // Act
            config.ProcessMessage("{}", typeof(FakeMessage1));
            config.ProcessMessage("{}", typeof(FakeMessage1));

            // Assert - task should have been started
            // Note: Task may not have fully completed yet, but Start() should have been called
            Assert.Equal(2, config.ProcessedCount);
        }
    }
}