using Xunit;

namespace ServiceConnect.EndToEndTests.Fixtures;

[CollectionDefinition(nameof(MessagingCollection))]
public class MessagingCollection : ICollectionFixture<MessagingFixture>
{
}

[CollectionDefinition(nameof(PersistenceCollection))]
public class PersistenceCollection : ICollectionFixture<PersistenceFixture>
{
}
