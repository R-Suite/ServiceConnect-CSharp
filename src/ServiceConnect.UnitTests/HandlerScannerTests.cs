using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

// Test types defined here for scanning
public class TestScannerMessage(Guid correlationId) : Message(correlationId)
{
}

public class TestScannerHandler : IMessageHandler<TestScannerMessage>
{
    public IConsumeContext Context { get; set; } = null!;
    public Task HandleAsync(TestScannerMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public abstract class AbstractTestHandler : IMessageHandler<TestScannerMessage>
{
    public IConsumeContext Context { get; set; } = null!;
    public abstract Task HandleAsync(TestScannerMessage message, CancellationToken cancellationToken = default);
}

public class HandlerScannerTests
{
    private readonly IEnumerable<Assembly> _testAssemblies;

    public HandlerScannerTests()
    {
        _testAssemblies = [typeof(HandlerScannerTests).Assembly];
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
        var results = HandlerScanner.ScanForHandlers([]);

        Assert.Empty(results);
    }

    [Fact]
    public void ScanForHandlers_AssemblyThrowsReflectionTypeLoadException_LogsWarning()
    {
        // L3: Silent swallow hides broken-assembly scan failures until a message arrives with
        // no handler. Add an optional logger overload; assert the warning is emitted.
        var logger = new Mock<ILogger>();
        var loaderException = new TypeLoadException("Could not resolve 'SomeMissingDependency'");
        var fakeAssembly = new Mock<Assembly>();
        fakeAssembly
            .Setup(a => a.GetTypes())
            .Throws(new ReflectionTypeLoadException(
                [typeof(string), null!],
                [loaderException]));
        fakeAssembly.SetupGet(a => a.FullName).Returns("Broken.Assembly, Version=1.0.0.0");

        // Should NOT throw; partial list is still returned.
        var results = HandlerScanner.ScanForHandlers([fakeAssembly.Object], logger.Object);

        Assert.Empty(results);  // No handlers in the one resolvable Type (typeof(string)).

        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) =>
                v.ToString()!.Contains("Broken.Assembly") && v.ToString()!.Contains("partial")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
