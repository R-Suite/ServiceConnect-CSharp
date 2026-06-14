using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Handlers;

public class HandlerScannerExceptionBreadthTests
{
    [Fact]
    public void ScanForHandlers_AssemblyThrowsFileNotFoundException_ContinuesAndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var good = typeof(HandlerScannerExceptionBreadthTests).Assembly;
        var bad = new ThrowingAssembly(new FileNotFoundException("missing dep"));

        // ScanForHandlers iterates the input enumerable. Pass [bad, good] so the bad
        // assembly's exception must not abort iteration — handlers in good must still
        // be discovered.
        var refs = HandlerScanner.ScanForHandlers([bad, good], loggerMock.Object);

        // Expect at least one handler from this test assembly.
        Assert.NotEmpty(refs);
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<FileNotFoundException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void ScanForHandlers_AssemblyThrowsBadImageFormatException_ContinuesAndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var bad = new ThrowingAssembly(new BadImageFormatException("native bitness mismatch"));
        var good = typeof(HandlerScannerExceptionBreadthTests).Assembly;

        var refs = HandlerScanner.ScanForHandlers([bad, good], loggerMock.Object);

        Assert.NotEmpty(refs);
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<BadImageFormatException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void ScanForHandlers_AssemblyThrowsTypeLoadException_ContinuesAndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var bad = new ThrowingAssembly(new TypeLoadException("type missing"));
        var good = typeof(HandlerScannerExceptionBreadthTests).Assembly;

        var refs = HandlerScanner.ScanForHandlers([bad, good], loggerMock.Object);

        Assert.NotEmpty(refs);
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<TypeLoadException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void ScanForHandlers_AssemblyThrowsFileLoadException_ContinuesAndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var bad = new ThrowingAssembly(new FileLoadException("version drift"));
        var good = typeof(HandlerScannerExceptionBreadthTests).Assembly;

        var refs = HandlerScanner.ScanForHandlers([bad, good], loggerMock.Object);

        Assert.NotEmpty(refs);
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<FileLoadException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Test double: an Assembly subclass whose GetTypes() throws the configured exception.
    /// Suppresses analyzer warnings about constructing a custom Assembly because the only
    /// surface we override is GetTypes()/FullName, which is what HandlerScanner reads.
    /// </summary>
    private sealed class ThrowingAssembly(Exception toThrow) : System.Reflection.Assembly
    {
        public override Type[] GetTypes() => throw toThrow;
        public override string? FullName => "ThrowingAssembly";
    }
}
