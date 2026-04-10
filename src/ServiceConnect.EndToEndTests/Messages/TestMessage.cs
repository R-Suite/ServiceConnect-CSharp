using ServiceConnect.Interfaces;

namespace ServiceConnect.EndToEndTests.Messages;

public class TestMessage : Message
{
    public TestMessage(Guid correlationId) : base(correlationId) { }

    public string? Content { get; set; }
}

public class TestRequest : Message
{
    public TestRequest(Guid correlationId) : base(correlationId) { }

    public string? RequestData { get; set; }
}

public class TestResponse : Message
{
    public TestResponse(Guid correlationId) : base(correlationId) { }

    public string? ResponseData { get; set; }
}

public class PriorityMessage : Message
{
    public PriorityMessage(Guid correlationId) : base(correlationId) { }

    public int Priority { get; set; }

    public string? Content { get; set; }
}

public class StepMessage : Message
{
    public StepMessage(Guid correlationId) : base(correlationId) { }

    public int Step { get; set; }

    public string? Content { get; set; }
}
