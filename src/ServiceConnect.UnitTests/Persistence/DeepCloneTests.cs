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
