using System;
using System.Collections.Generic;
using System.Threading;
using Moq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class ExpiredTimeoutsPollerTest
    {
        readonly Mock<IProcessManagerFinder> _mockProcessManagerFinder = new Mock<IProcessManagerFinder>();
        readonly Mock<IConfiguration> _mockConfiguration = new Mock<IConfiguration>();
        readonly Mock<IBus> _mockBus = new Mock<IBus>();
        private readonly Guid _tdId = Guid.NewGuid();
        private readonly Guid _pmId = Guid.NewGuid();

        public ExpiredTimeoutsPollerTest()
        {
            _mockConfiguration.Setup(c => c.GetProcessManagerFinder()).Returns(_mockProcessManagerFinder.Object);
            _mockBus.SetupGet(c => c.Configuration).Returns(_mockConfiguration.Object);
        }

        [Fact]
        public void TestTimeoutMessageIsDispatched()
        {
            // Arrange
            SetupProcessManagerFinderMock(DateTime.UtcNow.AddSeconds(30));
            var expiredTimeoutsPoller = new ExpiredTimeoutsPoller(_mockBus.Object);

            // Act
            expiredTimeoutsPoller.InnerPoll(new CancellationToken(false));

            // Assert
            _mockBus.Verify(i => i.Send("TestDest", It.Is<TimeoutMessage>(p => p.CorrelationId == _pmId), null), Times.Once);
        }

        [Fact]
        public void TestDispatchedTimeoutMessageIsRemoved()
        {
            // Arrange
            SetupProcessManagerFinderMock(DateTime.UtcNow.AddSeconds(30));
            var expiredTimeoutsPoller = new ExpiredTimeoutsPoller(_mockBus.Object);

            // Act
            expiredTimeoutsPoller.InnerPoll(new CancellationToken(false));

            // Assert
            _mockProcessManagerFinder.Verify(i => i.RemoveDispatchedTimeout(_tdId), Times.Once);
        }

        [Fact]
        public void TestNextQueryTimeIsResetToNextTimeoutDue()
        {
            // Arrange
            var nextTimeoutQueryTime = DateTime.UtcNow.AddSeconds(30);
            SetupProcessManagerFinderMock(nextTimeoutQueryTime);
            var expiredTimeoutsPoller = new ExpiredTimeoutsPoller(_mockBus.Object);

            // Act
            expiredTimeoutsPoller.InnerPoll(new CancellationToken(false));

            // Assert
            Assert.Equal(nextTimeoutQueryTime, expiredTimeoutsPoller.NextQueryUtc);
        }

        [Fact]
        public void TestNextQueryTimeIsResetToMaxAlowedValue()
        {
            // Arrange
            var nextTimeoutQueryTime = DateTime.UtcNow.AddDays(1);
            SetupProcessManagerFinderMock(nextTimeoutQueryTime);
            var expiredTimeoutsPoller = new ExpiredTimeoutsPoller(_mockBus.Object);

            // Act
            expiredTimeoutsPoller.InnerPoll(new CancellationToken(false));

            // Assert
            Assert.True(expiredTimeoutsPoller.NextQueryUtc < nextTimeoutQueryTime);
        }

        [Fact]
        public void TestNextQueryTimeIsResetWhenNewTimeoutIsInserted()
        {
            // Arrange
            var expiredTimeoutsPoller = new ExpiredTimeoutsPoller(_mockBus.Object);// todo: pass time provider to make testing easier
            var nextTimeoutQueryTime = DateTime.UtcNow.AddSeconds(-10);

            // Act
            _mockProcessManagerFinder.Raise(e => e.TimeoutInserted += null, nextTimeoutQueryTime);

            // Assert
            Assert.Equal(expiredTimeoutsPoller.NextQueryUtc, nextTimeoutQueryTime);
        }

        private void SetupProcessManagerFinderMock(DateTime nextTimeoutQueryTime)
        {
            var timeoutsBatch = new TimeoutsBatch
            {
                DueTimeouts =
                    new List<TimeoutData>
                    {
                        new TimeoutData
                        {
                            Time = DateTime.UtcNow.AddSeconds(-30),
                            ProcessManagerId = _pmId,
                            Id = _tdId,
                            Destination = "TestDest"
                        }
                    },
                NextQueryTime = nextTimeoutQueryTime
            };

            _mockProcessManagerFinder.Setup(i => i.GetTimeoutsBatch()).Returns(timeoutsBatch);
        }
    }
}
