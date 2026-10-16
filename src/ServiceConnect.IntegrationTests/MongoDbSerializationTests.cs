using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using ServiceConnect.Persistance.MongoDb;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    /// <summary>
    /// The Guid serializer is process-wide state, so these tests drive the decision through fakes.
    /// </summary>
    public class MongoDbSerializationTests
    {
        [Fact]
        public void ShouldRegisterCSharpLegacyWhenNoGuidSerializerIsRegistered()
        {
            GuidSerializer registered = null;

            MongoDbSerialization.EnsureGuidRepresentation(s => { registered = s; return true; }, () => throw new InvalidOperationException("not called"));

            Assert.Equal(GuidRepresentation.CSharpLegacy, registered.GuidRepresentation);
        }

        [Theory]
        [InlineData(GuidRepresentation.Standard)]
        [InlineData(GuidRepresentation.CSharpLegacy)]
        [InlineData(GuidRepresentation.JavaLegacy)]
        public void ShouldKeepTheApplicationsGuidSerializer(GuidRepresentation representation)
        {
            var exception = Record.Exception(() => MongoDbSerialization.EnsureGuidRepresentation(
                _ => throw new BsonSerializationException("There is already a different serializer registered for type Guid."),
                () => new GuidSerializer(representation)));

            Assert.Null(exception);
        }

        [Fact]
        public void ShouldExplainHowToFixWhenTheDefaultSerializerIsAlreadyInUse()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MongoDbSerialization.EnsureGuidRepresentation(
                _ => throw new BsonSerializationException("There is already a different serializer registered for type Guid."),
                () => new GuidSerializer(GuidRepresentation.Unspecified)));

            Assert.Contains("BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy))", exception.Message);
        }
    }
}
