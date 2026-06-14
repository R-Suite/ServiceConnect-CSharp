using ServiceConnect.Interfaces;

namespace ServiceConnect.EndToEndTests.Messages;

public class TestData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public string Name { get; set; } = string.Empty;
}
