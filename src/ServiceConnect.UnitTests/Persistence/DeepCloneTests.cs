using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class DeepCloneTests
{
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
        // BSON's _t discriminator must capture runtime element types inside collections
        // so a Dog inside a List<Animal> round-trips with Dog.Breed intact rather than
        // being collapsed to Animal on deserialize.
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
    public void Clone_RootIsCollection_RoundTripsViaWrapper()
    {
        // BSON refuses to write arrays / collections at the document root. Header values
        // on TimeoutData legitimately arrive as List<byte> / string[] / Dictionary<,>;
        // the wrapper-document strategy lets these round-trip without callers having to
        // know about the BSON limitation.
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

    [Fact]
    public void Clone_PreservesExplicitInterfaceAutoProperty()
    {
        // M39 regression: explicit-interface auto-properties round-trip with default(Guid)
        // under Newtonsoft.Json (which only saw public properties by short name). BSON's
        // BsonClassMap discovers them.
        var original = new HasExplicitInterfaceAutoProp { ExplicitFooId = Guid.Parse("11111111-2222-3333-4444-555555555555") };

        var clone = DeepClone.Clone(original);

        Assert.Equal(original.ExplicitFooId, clone.ExplicitFooId);
        Assert.Equal(original.ExplicitFooId, ((IHasFooId)clone).FooId);
    }

    private interface IHasFooId
    {
        Guid FooId { get; set; }
    }

    private sealed class HasExplicitInterfaceAutoProp : IHasFooId
    {
        public Guid ExplicitFooId { get; set; }
        Guid IHasFooId.FooId
        {
            get => ExplicitFooId;
            set => ExplicitFooId = value;
        }
    }
}
