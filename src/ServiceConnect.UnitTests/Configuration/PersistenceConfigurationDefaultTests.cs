using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

public class PersistenceConfigurationDefaultTests
{
    [Fact]
    public void ConnectionString_DefaultsToEmpty()
    {
        var config = new PersistenceConfiguration();
        Assert.Equal(string.Empty, config.ConnectionString);
    }

    [Fact]
    public void ConnectionString_NoLongerDefaultsToLocalhost()
    {
        // Pin: a regression that re-introduces a localhost default for ConnectionString
        // surfaces immediately rather than as a silent production accident.
        var config = new PersistenceConfiguration();
        Assert.NotEqual("mongodb://localhost/", config.ConnectionString);
    }

    [Fact]
    public void DatabaseName_DefaultPreserved()
    {
        // The DatabaseName default is unchanged — only ConnectionString changes.
        var config = new PersistenceConfiguration();
        Assert.Equal("RMessageBusPersistentStore", config.DatabaseName);
    }
}
