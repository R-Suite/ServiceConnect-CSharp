using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

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
        // Pin: this asserts the v8 contract change. A regression that re-introduces the
        // localhost default surfaces immediately rather than as a silent production accident.
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
