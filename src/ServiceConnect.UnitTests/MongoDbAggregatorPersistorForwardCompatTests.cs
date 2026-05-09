using System;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorForwardCompatTests
{
    private static Type GetAggregatorDocumentType()
    {
        var type = typeof(MongoDbAggregatorPersistor)
            .GetNestedType("AggregatorDocument", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return type!;
    }

    [Fact]
    public void AggregatorDocument_Deserialize_ToleratesUnknownField()
    {
        var documentType = GetAggregatorDocumentType();

        // Construct a BSON document with all known fields plus an extra one that
        // a newer worker might add. Without [BsonIgnoreExtraElements] this throws
        // FormatException on the unknown element.
        var bson = new BsonDocument
        {
            { "_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
            { "Version", 1 },
            { "DataBson", new BsonDocument("X", 1) },
            { "DataTypeName", typeof(object).AssemblyQualifiedName ?? "object" },
            { "Name", "agg-forward" },
            { "InsertedAtTicks", 0L },
            { "FutureField", "added-by-newer-worker" }
        };

        var result = BsonSerializer.Deserialize(bson, documentType);

        Assert.NotNull(result);
    }
}
