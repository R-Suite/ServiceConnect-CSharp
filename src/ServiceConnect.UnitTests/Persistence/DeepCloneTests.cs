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
        // TypeNameHandling.Auto must be used so runtime element types inside collections
        // survive the round-trip — TypeNameHandling.None would collapse a Dog inside a
        // List<Animal> to Animal on deserialize and lose Dog.Breed.
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
