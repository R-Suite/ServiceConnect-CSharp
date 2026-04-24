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
        public List<Animal> Pets { get; set; } = new();
    }

    [Fact]
    public void Clone_CollectionElementIsSubclass_PreservesSubclassType()
    {
        // L13: TypeNameHandling.None collapsed List<Animal>'s Dog element to Animal on
        // deserialize, losing Dog.Breed. Fix switches to TypeNameHandling.Auto so runtime
        // types inside collections survive the round-trip.
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
}
