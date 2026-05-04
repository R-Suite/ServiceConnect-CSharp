using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

// Write(ReadOnlyMemory<byte>, long) replaced Write(byte[], long); the null-argument
// guard test has been removed because ReadOnlyMemory<byte> is a value type and cannot
// be null. Default(ReadOnlyMemory<byte>) / ReadOnlyMemory<byte>.Empty produces an
// empty packet, which is valid behaviour.
public class MessageBusReadStreamGuardTests
{
}
