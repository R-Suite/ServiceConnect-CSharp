using System.Globalization;
using ServiceConnect.Interfaces.Exceptions;
using Xunit;

namespace ServiceConnect.UnitTests.Exceptions;

public class RequestTimeoutExceptionCultureTests
{
    [Fact]
    public void Message_UsesInvariantCultureForElapsedFormatting()
    {
        // Save and restore the thread culture to avoid leaking state to sibling tests.
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var ex = new RequestTimeoutException(Guid.NewGuid(), TimeSpan.FromMilliseconds(123.456));
            // de-DE uses ',' as decimal separator. Invariant uses '.'.
            // The message must NOT contain a comma in the milliseconds value.
            Assert.DoesNotContain(",", ex.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }
}
