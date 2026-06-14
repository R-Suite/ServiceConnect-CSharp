using System.Text.Json.Serialization;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class DeepCloneTests
{
    // Polymorphic types need a discriminator declared on the base for STJ to round-trip
    // derived elements inside a base-typed collection. Saga authors with polymorphic
    // state must annotate the base similarly; without the annotation the derived
    // properties collapse to the declared type on read.
    [JsonDerivedType(typeof(Dog), "dog")]
    [JsonDerivedType(typeof(Animal), "animal")]
    public class Animal { public string Name { get; set; } = ""; }
    public class Dog : Animal { public string Breed { get; set; } = ""; }

    public class Owner
    {
        public Guid Id { get; set; }
        public List<Animal> Pets { get; set; } = [];
    }

    [Fact]
    public void Clone_CollectionElementIsSubclass_PreservesSubclassType()
    {
        // STJ's [JsonDerivedType] discriminator captures the runtime element type inside
        // a base-typed collection so a Dog inside a List<Animal> round-trips with Dog.Breed
        // intact rather than being collapsed to Animal on deserialize.
        var owner = new Owner
        {
            Id = Guid.NewGuid(),
            Pets = { new Dog { Name = "Rex", Breed = "Labrador" } },
        };

        var clone = DeepClone.Clone(owner);

        Assert.Single(clone.Pets);
        var dog = Assert.IsType<Dog>(clone.Pets[0]);
        Assert.Equal("Labrador", dog.Breed);
    }

    [Fact]
    public void Clone_RootIsCollection_RoundTripsCollectionTypes()
    {
        // Header values on TimeoutData legitimately arrive as List<byte> / string[] /
        // Dictionary<,>. STJ round-trips each of these at the document root without
        // any wrapper, unlike the previous BSON-backed implementation which had to
        // wrap collection roots because BSON refused them.
        var list = new List<byte> { 1, 2, 3 };
        var listClone = DeepClone.Clone(list);
        Assert.NotSame(list, listClone);
        Assert.Equal(new byte[] { 1, 2, 3 }, listClone);

        var array = new[] { "a", "b", "c" };
        var arrayClone = DeepClone.Clone(array);
        Assert.NotSame(array, arrayClone);
        Assert.Equal(array, arrayClone);

        var dict = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
        var dictClone = DeepClone.Clone(dict);
        Assert.NotSame(dict, dictClone);
        Assert.Equal(2, dictClone.Count);
        Assert.Equal(1, dictClone["one"]);
        Assert.Equal(2, dictClone["two"]);
    }
}
