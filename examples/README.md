# ServiceConnect Examples

This area contains runnable console applications for the supported messaging and workflow patterns in ServiceConnect.

## Patterns

`PointToPoint`, `PublishSubscribe`, `RequestReply`, `CompetingConsumers`, `ContentBasedRouting`, `RoutingSlip`, `ScatterGather`, `Aggregator`, `ProcessManager`, `Filters`, and `Streaming` are implemented and runnable now.

- [PointToPoint](./PointToPoint/)
- [PublishSubscribe](./PublishSubscribe/)
- [RequestReply](./RequestReply/)
- [CompetingConsumers](./CompetingConsumers/)
- [ContentBasedRouting](./ContentBasedRouting/)
- [RoutingSlip](./RoutingSlip/)
- [ScatterGather](./ScatterGather/)
- [Aggregator](./Aggregator/)
- [ProcessManager](./ProcessManager/)
- [Filters](./Filters/)
- [Streaming](./Streaming/)

## Shared Dependencies

Shared dependencies are documented here ahead of the runnable examples. From the repository root, start RabbitMQ and MongoDB with:

```bash
docker compose -f examples/docker-compose.yml up -d
```
