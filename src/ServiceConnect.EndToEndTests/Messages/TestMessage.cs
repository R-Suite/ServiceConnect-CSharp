using ServiceConnect.Interfaces;

namespace ServiceConnect.EndToEndTests.Messages;

public class TestMessage(Guid correlationId) : Message(correlationId)
{
    public string Content { get; set; } = string.Empty;
}

public class TestRequest(Guid correlationId) : Message(correlationId)
{
    public string Question { get; set; } = string.Empty;
}

public class TestResponse(Guid correlationId) : Message(correlationId)
{
    public string Answer { get; set; } = string.Empty;
}

public class PriorityMessage(Guid correlationId) : Message(correlationId)
{
    public int Priority { get; set; }
    public int Order { get; set; }
}

public class StepMessage(Guid correlationId) : Message(correlationId)
{
    public List<string> VisitedSteps { get; set; } = [];
    public string CurrentStep { get; set; } = string.Empty;
}

public class DerivedTestMessage(Guid correlationId) : TestMessage(correlationId)
{
    public string Extra { get; set; } = string.Empty;
}

public class TestProcessData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
    public string LastContent { get; set; } = string.Empty;
}
