using Xunit;

namespace ServiceConnect.EndToEndTests.Fixtures;

/// <summary>
/// Tests that register TestMessage handlers must run sequentially — they share
/// the TestMessage fanout exchange and would cross-contaminate in parallel.
/// </summary>
[CollectionDefinition(nameof(MessagingCollection))]
public class MessagingCollection : ICollectionFixture<MessagingFixture>
{
}

/// <summary>
/// Tests that only use TestRequest/TestResponse message types.
/// Safe to run in parallel with MessagingCollection since they bind to different exchanges.
/// </summary>
[CollectionDefinition(nameof(RequestReplyCollection))]
public class RequestReplyCollection : ICollectionFixture<MessagingFixture>
{
}

/// <summary>
/// Tests that use unique message types (PriorityMessage, StepMessage, etc.)
/// or don't bind to any exchange. Safe to run in parallel.
/// </summary>
[CollectionDefinition(nameof(IsolatedCollection))]
public class IsolatedCollection : ICollectionFixture<MessagingFixture>
{
}

/// <summary>
/// Tests requiring MongoDB persistence.
/// </summary>
[CollectionDefinition(nameof(PersistenceCollection))]
public class PersistenceCollection : ICollectionFixture<PersistenceFixture>
{
}
