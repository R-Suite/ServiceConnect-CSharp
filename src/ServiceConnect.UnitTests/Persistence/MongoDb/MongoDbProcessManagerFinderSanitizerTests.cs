using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
public class MongoDbProcessManagerFinderSanitizerTests
{
    [Theory]
    [InlineData("Foo.Bar.Baz", "Foo.Bar.Baz")]                                       // non-generic, no change
    [InlineData("Foo+Nested", "Foo_Nested")]                                          // nested type
    [InlineData("Foo`1[[Bar.Baz, MyAssembly]]", "Foo_1__Bar.Baz_ MyAssembly__")]     // generic (space inside brackets is not replaced)
    [InlineData("Has,Comma", "Has_Comma")]                                            // comma
    public void SanitizeCollectionName_ReplacesIllegalChars(string raw, string expected)
    {
        var result = MongoDbProcessManagerFinder.SanitizeCollectionName(raw);
        Assert.Equal(expected, result);
    }
}
