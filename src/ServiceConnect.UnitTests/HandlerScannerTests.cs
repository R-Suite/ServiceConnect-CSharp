using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests
{
    // Test types defined here for scanning
    public class TestScannerMessage : Message
    {
        public TestScannerMessage(Guid correlationId) : base(correlationId) { }
    }

    public class TestScannerHandler : IMessageHandler<TestScannerMessage>
    {
        public IConsumeContext? Context { get; set; }
        public Task HandleAsync(TestScannerMessage message) => Task.CompletedTask;
    }

    public abstract class AbstractTestHandler : IMessageHandler<TestScannerMessage>
    {
        public IConsumeContext? Context { get; set; }
        public abstract Task HandleAsync(TestScannerMessage message);
    }

    public class HandlerScannerTests
    {
        private readonly IEnumerable<Assembly> _testAssemblies;

        public HandlerScannerTests()
        {
            _testAssemblies = new[] { typeof(HandlerScannerTests).Assembly };
        }

        [Fact]
        public void ScanForHandlers_FindsHandlerInAssembly()
        {
            var results = HandlerScanner.ScanForHandlers(_testAssemblies);

            Assert.Contains(results, r => r.HandlerType == typeof(TestScannerHandler));
        }

        [Fact]
        public void ScanForHandlers_ReturnsCorrectMessageType()
        {
            var results = HandlerScanner.ScanForHandlers(_testAssemblies);

            var handlerRef = results.FirstOrDefault(r => r.HandlerType == typeof(TestScannerHandler));
            Assert.NotNull(handlerRef);
            Assert.Equal(typeof(TestScannerMessage), handlerRef.MessageType);
        }

        [Fact]
        public void ScanForHandlers_IgnoresAbstractClasses()
        {
            var results = HandlerScanner.ScanForHandlers(_testAssemblies);

            Assert.DoesNotContain(results, r => r.HandlerType == typeof(AbstractTestHandler));
        }

        [Fact]
        public void ScanForHandlers_IgnoresInterfaces()
        {
            var results = HandlerScanner.ScanForHandlers(_testAssemblies);

            Assert.All(results, r => Assert.False(r.HandlerType.IsInterface));
        }

        [Fact]
        public void ScanForHandlers_EmptyAssemblies_ReturnsEmpty()
        {
            var results = HandlerScanner.ScanForHandlers(Enumerable.Empty<Assembly>());

            Assert.Empty(results);
        }
    }
}
