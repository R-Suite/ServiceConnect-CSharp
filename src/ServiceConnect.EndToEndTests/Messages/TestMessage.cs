using ServiceConnect.Interfaces;

namespace ServiceConnect.EndToEndTests.Messages;

public class TestMessage : Message
{
    public TestMessage(Guid correlationId) : base(correlationId) { }
    public string Content { get; set; } = string.Empty;
}

public class TestRequest : Message
{
    public TestRequest(Guid correlationId) : base(correlationId) { }
    public string Question { get; set; } = string.Empty;
}

public class TestResponse : Message
{
    public TestResponse(Guid correlationId) : base(correlationId) { }
    public string Answer { get; set; } = string.Empty;
}

public class PriorityMessage : Message
{
    public PriorityMessage(Guid correlationId) : base(correlationId) { }
    public int Priority { get; set; }
    public int Order { get; set; }
}

public class StepMessage : Message
{
    public StepMessage(Guid correlationId) : base(correlationId) { }
    public List<string> VisitedSteps { get; set; } = new();
    public string CurrentStep { get; set; } = string.Empty;
}
