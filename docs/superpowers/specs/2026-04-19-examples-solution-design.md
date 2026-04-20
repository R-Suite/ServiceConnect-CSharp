# ServiceConnect Examples Solution Design

## Goal

Create a new `examples/` area that showcases every currently supported ServiceConnect messaging pattern as runnable C# console applications. Each pattern will live in its own solution folder, start the required shared containerized dependencies, and include a pattern-specific README with a Mermaid diagram that explains the message flow.

## Scope

This design covers messaging-pattern examples only. It does not attempt to document every operational feature in the product.

Initial pattern set:

- Point-to-Point
- Publish/Subscribe
- Request/Reply
- Competing Consumers
- Process Manager
- Routing Slip
- Scatter/Gather
- Aggregator
- Filters
- Streaming
- Content-Based Routing

If exploration during implementation finds an additional supported messaging pattern already present in the codebase, it should be added to the examples area using the same structure.

## Design Principles

- Each pattern is isolated and understandable on its own.
- Each example uses normal console apps, not test-only harnesses.
- Each example remains manually runnable, even when a convenience runner is present.
- Shared infrastructure is started once and reused across patterns.
- Shared sample code is centralized to reduce duplication.
- Documentation is part of the deliverable, not an afterthought.

## Top-Level Structure

The new top-level layout will be:

```text
examples/
  README.md
  docker-compose.yml
  Directory.Build.props
  ExampleSupport/
    ServiceConnect.Examples.Support.csproj
    ...support code...
  PointToPoint/
    PointToPoint.sln
    README.md
    run.sh
    run.ps1
    src/
      ServiceConnect.Examples.PointToPoint.Contracts/
      ServiceConnect.Examples.PointToPoint.Sender/
      ServiceConnect.Examples.PointToPoint.Consumer/
  PublishSubscribe/
    ...same pattern-specific shape...
  RequestReply/
  CompetingConsumers/
  ProcessManager/
  RoutingSlip/
  ScatterGather/
  Aggregator/
  Filters/
  Streaming/
  ContentBasedRouting/
```

## Per-Pattern Structure

Every pattern folder will contain its own solution and only the projects needed to demonstrate that topology.

Common elements:

- `<PatternName>.sln`
- `README.md`
- `run.sh`
- `run.ps1`
- `src/` directory containing projects

Common project types:

- `Contracts` project for shared messages and DTOs
- one console application per participating endpoint role
- optional pattern-local helper project only when the pattern needs additional code that does not belong in shared support

Examples of role shape:

- Point-to-Point: `Sender`, `Consumer`
- Publish/Subscribe: `Publisher`, `SubscriberA`, `SubscriberB`
- Request/Reply: `Requester`, `Responder`
- Competing Consumers: `Producer`, `WorkerA`, `WorkerB`
- Routing Slip: `Starter`, `StepA`, `StepB`, `StepC`
- Scatter/Gather: `Requester`, `ResponderA`, `ResponderB`
- Aggregator: `ProducerA`, `ProducerB`, `AggregatorConsumer`
- Process Manager: coordinator-triggering endpoint plus workflow participants
- Filters: sender/consumer endpoints with explicit filter registration
- Streaming: `Uploader`, `Receiver`
- Content-Based Routing: sender plus multiple specialized consumers

The design intentionally avoids forcing a fixed two-project shape, because some supported patterns require three or more active endpoints.

## Shared Example Support Project

`examples/ExampleSupport/ServiceConnect.Examples.Support.csproj` will contain reusable example infrastructure:

- shared configuration model for RabbitMQ and MongoDB
- configuration loading from appsettings and environment variables
- standard console logging setup
- helper methods for bootstrapping the ServiceConnect bus
- common endpoint naming and queue naming helpers
- dependency readiness checks for RabbitMQ and MongoDB
- shared console output helpers for success markers and pattern narration

This project exists to keep the pattern projects short and focused on the messaging pattern itself.

## Dependency Model

Shared infrastructure will be defined once in `examples/docker-compose.yml`.

Initial shared services:

- RabbitMQ
- MongoDB

Rationale:

- RabbitMQ is required for all messaging examples.
- MongoDB is required for persistence-backed patterns such as Process Manager and Aggregator.
- A shared stack keeps the developer experience simple while avoiding repeated compose files.

The design remains hybrid-capable: if a future pattern requires an additional service, that service can be added without changing the overall examples model.

## Project Reference Strategy

Examples will use local project references into the existing `src/` tree rather than NuGet package references.

Rationale:

- the current version is not yet published
- examples need to reflect the exact in-repo implementation
- local references simplify iteration while the examples are being built alongside the library

## Configuration Strategy

All example apps will read from a shared configuration contract with values for:

- RabbitMQ host
- RabbitMQ port
- RabbitMQ username
- RabbitMQ password
- MongoDB connection string
- endpoint-specific logical names when needed

Configuration precedence should favor local overrides through environment variables so the examples remain easy to run in different environments.

## Runtime Model

Each pattern must support both of these workflows:

### One-command workflow

Each pattern folder includes `run.sh` and `run.ps1` that:

1. ensure the shared container stack is running
2. wait for required dependencies to become reachable
3. start consumer-side or passive endpoints first
4. start the initiating endpoint last
5. keep output visible so the user can observe the message flow

### Manual workflow

Users can also open the pattern solution or use `dotnet run` on each endpoint project independently.

This keeps the examples useful both as quick-start demos and as normal debuggable applications.

## Pattern Completion Behavior

Examples should be self-terminating where practical.

Recommended behavior:

- initiating apps exit after the expected interaction completes
- passive endpoints may either exit after the scripted scenario is complete or remain cancellable if the pattern is easier to understand as a running listener

The preferred default is a deterministic scripted run that demonstrates the pattern and then exits cleanly, because that produces a better first-run experience.

## Documentation Requirements

Each pattern folder must contain a `README.md` with the same section order:

1. Overview
2. Participants
3. Mermaid Diagram
4. Prerequisites
5. Run This Example
6. Run Manually
7. Expected Output
8. What To Notice

Each README must include a Mermaid diagram that matches the implemented topology.

Diagram guidance:

- use sequence diagrams when ordering matters, such as Request/Reply, Routing Slip, Process Manager, and Scatter/Gather
- use flowcharts when the topology is easier to understand spatially, such as Content-Based Routing or Competing Consumers
- use the same endpoint names in the diagram that appear in the project names and console output

The top-level `examples/README.md` will act as an index for all pattern folders and link to each example.

## Pattern Coverage Expectations

Each pattern README and code sample should demonstrate a focused, minimal, successful scenario.

Success criteria by pattern:

- Point-to-Point: one sender delivers one message to one consumer
- Publish/Subscribe: one publisher delivers one event to multiple subscribers
- Request/Reply: one requester receives a reply from one responder
- Competing Consumers: multiple workers compete for work from the same queue and the run visibly shows distribution
- Process Manager: a multi-step workflow progresses through each stage and completes
- Routing Slip: a message traverses each destination in sequence
- Scatter/Gather: a requester sends work to multiple responders and receives the expected set of responses
- Aggregator: multiple related messages are combined and the aggregated result is emitted or displayed
- Filters: custom pipeline behavior is visible before or after handling
- Streaming: a large payload is transferred successfully using streaming support
- Content-Based Routing: routing decisions visibly vary based on message content

## Verification Strategy

The first implementation should optimize for reliable runnable examples rather than fully automated end-to-end validation for every pattern.

Verification layers:

1. console applications that demonstrate the pattern for human users
2. shared smoke coverage for the examples infrastructure where practical

The first pass should not attempt to convert the full examples suite into a heavy automated orchestration framework. That can be added later if needed.

## Error Handling Expectations

Examples must fail clearly when infrastructure is unavailable.

Expected behavior:

- readiness checks should emit actionable errors if RabbitMQ or MongoDB are not reachable
- scripts should stop on startup failures rather than continuing with partial state
- console output should clearly identify which endpoint failed and why

The examples are intentionally instructional, so clarity of failure messages matters as much as successful output.

## Naming Conventions

- folders use pattern names, for example `PointToPoint` and `RequestReply`
- projects use `ServiceConnect.Examples.<Pattern>.<Role>`
- shared support project uses `ServiceConnect.Examples.Support`
- endpoint names used at runtime should be predictable and aligned with the folder terminology

## Non-Goals

This design does not include:

- a single global examples solution containing every project
- NuGet-based consumer packaging for the examples
- documentation for non-pattern operational features such as retry policy tuning or auditing internals
- a full CI orchestration harness for every example in the first implementation pass

## Implementation Notes

- follow the existing repo conventions for SDK style C# projects and target frameworks
- align example bootstrapping with the real ServiceConnect configuration APIs already used in `src/ServiceConnect.EndToEndTests`
- reuse existing pattern knowledge from the end-to-end tests to avoid inventing behavior that the library does not actually support
- keep each example minimal; avoid introducing extra abstractions beyond shared support and the pattern-local contracts project

## Open Decisions Resolved In This Design

- examples live under a new top-level `examples/` folder
- each pattern gets its own solution and project set
- each pattern is a runnable console-based topology, not a fixed sender/consumer pair
- examples use local project references
- shared infrastructure is containerized in a common compose file
- each pattern supports both one-command and manual execution
- each pattern includes a Mermaid-backed README

## Success Definition

This work is successful when a developer can:

1. navigate to `examples/<PatternName>`
2. start the example with one command
3. observe the full pattern behavior in console output
4. understand the message flow from the README and Mermaid diagram
5. run the participating endpoint apps manually if deeper debugging is needed
