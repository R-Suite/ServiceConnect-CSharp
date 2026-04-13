using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class DefaultProcessManagerPropertyMapperTests
{
    [Fact]
    public void ConfigureMapping_AddsMappingWithMessageType()
    {
        var mapper = new DefaultProcessManagerPropertyMapper();

        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.OrderId, m => m.OrderId);

        var mapping = Assert.Single(mapper.Mappings);
        Assert.Equal(typeof(FakePmMsg), mapping.MessageType);
    }

    [Fact]
    public void ConfigureMapping_UnwrapsUnaryExpression_ForValueTypeProperty()
    {
        // Guid -> object requires a boxing Convert expression; that means the body is a
        // UnaryExpression(Convert) around a MemberExpression. The mapper must unwrap it.
        var mapper = new DefaultProcessManagerPropertyMapper();

        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.OrderId, m => m.OrderId);

        var mapping = mapper.Mappings.Single();
        Assert.True(mapping.PropertiesHierarchy.ContainsKey(nameof(FakePmData.OrderId)));
        Assert.Equal(typeof(Guid), mapping.PropertiesHierarchy[nameof(FakePmData.OrderId)]);
    }

    [Fact]
    public void ConfigureMapping_HandlesDirectMemberExpression_ForReferenceTypeProperty()
    {
        // string -> object is reference-assignable, so no boxing Convert is synthesised;
        // the body is a MemberExpression directly.
        var mapper = new DefaultProcessManagerPropertyMapper();

        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.Customer, m => m.Customer);

        var mapping = mapper.Mappings.Single();
        Assert.True(mapping.PropertiesHierarchy.ContainsKey(nameof(FakePmData.Customer)));
        Assert.Equal(typeof(string), mapping.PropertiesHierarchy[nameof(FakePmData.Customer)]);
    }

    [Fact]
    public void ConfigureMapping_CompiledMessageFunc_ExtractsValueFromMessage()
    {
        var mapper = new DefaultProcessManagerPropertyMapper();
        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.Customer, m => m.Customer);

        var msg = new FakePmMsg(Guid.NewGuid()) { Customer = "Acme" };
        var value = mapper.Mappings.Single().MessageProp(msg);

        Assert.Equal("Acme", value);
    }
}

file class FakePmData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public Guid OrderId { get; set; }
    public string Customer { get; set; } = "";
}

file class FakePmMsg : Message
{
    public FakePmMsg(Guid c) : base(c) { }
    public Guid OrderId { get; set; }
    public string Customer { get; set; } = "";
}
