# Health Checks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a `ServiceConnect.HealthChecks` package with three opt-in `IHealthCheck` classes (bus liveness, consumer connection, producer connection), promote `IProducer.IsHealthy` on the existing `ServiceConnect.Interfaces` interface, and rewrite the observability doc's "No health checks" section to cover the new shipped reality.

**Architecture:** One new transport-agnostic package (`ServiceConnect.HealthChecks`) depending only on `ServiceConnect.Interfaces` and `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`. Three `IHealthCheck` classes resolve `IBus`/`IConsumer`/`IProducer` and inspect existing public properties — no I/O, no broker probes. Three matching extension methods on `IHealthChecksBuilder` register one check each. Multi-targets `net8.0;net10.0` from `Directory.Build.props`.

**Tech Stack:** .NET 8 + .NET 10 multi-target, C# 12 / C# 14, xUnit, Moq, `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` 9.0.0, Testcontainers RabbitMQ (existing E2E fixture).

---

## Spec drift recorded here

The committed spec at `docs/superpowers/specs/2026-04-28-health-checks-design.md` describes an integration test that drops the RabbitMQ connection and re-asserts. The existing E2E suite has **no broker-drop helper** ([Fixtures/MessagingFixture.cs](src/ServiceConnect.EndToEndTests/Fixtures/MessagingFixture.cs) just spins up `RabbitMqContainer`; no `Pause`/`Stop`/management-API integration). Building one is its own piece of work, out of scope for this plan. The integration test in this plan exercises the positive wiring path only (start everything, all three checks Healthy). The negative path (Unhealthy when the underlying property is false) is fully covered by the unit tests in tasks 3–5. This narrowing is consistent with the spec's "asserts steady states only — never transitions" guidance.

---

### Task 1: Promote `IProducer.IsHealthy` to public surface

Add a `bool IsHealthy { get; }` property to `IProducer` in `ServiceConnect.Interfaces`, mirroring the existing `IConsumer.IsConnected`. The RabbitMQ `Producer` implementation delegates to its private `_producerConnection.IsHealthy()`.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IProducer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:22` (existing `_producerConnection` field is at line 22; add the property near other public members)
- Test: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerIsHealthyTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerIsHealthyTests.cs`:

```csharp
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="Producer.IsHealthy"/> reflects the underlying
/// <c>ProducerConnection.IsHealthy()</c> state — the public surface that
/// <c>ProducerConnectionHealthCheck</c> observes.
/// </summary>
public class ProducerIsHealthyTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static object GetProducerConnection(Producer producer)
    {
        var field = typeof(Producer).GetField("_producerConnection",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(producer)!;
    }

    private static void SetField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(target, value);
    }

    [Fact]
    public void IsHealthy_FreshProducer_ReturnsFalse()
    {
        var producer = CreateProducer();
        Assert.False(producer.IsHealthy);
    }

    [Fact]
    public void IsHealthy_ConnectedAndChannelOpen_ReturnsTrue()
    {
        var producer = CreateProducer();
        var connection = GetProducerConnection(producer);

        var openChannel = new Mock<IChannel>();
        openChannel.SetupGet(c => c.IsOpen).Returns(true);

        SetField(connection, "_connected", true);
        SetField(connection, "_model", openChannel.Object);

        Assert.True(producer.IsHealthy);
    }

    [Fact]
    public void IsHealthy_ConnectedButChannelClosed_ReturnsFalse()
    {
        var producer = CreateProducer();
        var connection = GetProducerConnection(producer);

        var closedChannel = new Mock<IChannel>();
        closedChannel.SetupGet(c => c.IsOpen).Returns(false);

        SetField(connection, "_connected", true);
        SetField(connection, "_model", closedChannel.Object);

        Assert.False(producer.IsHealthy);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~ProducerIsHealthyTests -v minimal`

Expected: BUILD FAILURE — `'Producer' does not contain a definition for 'IsHealthy'`.

- [ ] **Step 3: Add the property to the interface**

Edit `src/ServiceConnect.Interfaces/Bus/IProducer.cs`. Add the new property after the existing `MaximumMessageSize` property (keeps related state-observation members grouped). Insert before the closing brace of the interface:

```csharp
    /// <summary>
    /// Gets whether the producer is currently connected and ready to publish or send.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> before the first publish/send call (the producer
    /// connects lazily) and after a connection drop until the next reconnect. Mirrors
    /// <see cref="IConsumer.IsConnected"/>.
    /// </remarks>
    bool IsHealthy { get; }
```

- [ ] **Step 4: Implement the property on Producer**

Edit `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`. Locate the public surface area (around `MaximumMessageSize`, which is the existing `IProducer` member) and add directly above or below it:

```csharp
    /// <inheritdoc />
    public bool IsHealthy => _producerConnection.IsHealthy();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~ProducerIsHealthyTests -v minimal`

Expected: 3 tests pass.

- [ ] **Step 6: Run the full RabbitMQ test slice to confirm no regressions**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~ServiceConnect.UnitTests.RabbitMQ -v minimal`

Expected: all RabbitMQ unit tests pass (existing + 3 new).

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IProducer.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerIsHealthyTests.cs
git commit -m "feat(interfaces): add IProducer.IsHealthy mirroring IConsumer.IsConnected

Expose the existing ProducerConnection.IsHealthy() reading on the public
IProducer interface so the upcoming ServiceConnect.HealthChecks package
can observe producer connection state without reaching into RabbitMQ
internals.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Create the `ServiceConnect.HealthChecks` project skeleton

Create the package csproj, nuspec, add to `slnx`. No checks yet — confirms build, packaging, and solution wiring in isolation before any logic lands.

**Files:**
- Create: `src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj`
- Create: `src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.nuspec`
- Create: `src/ServiceConnect.HealthChecks/AssemblyMarker.cs` (placeholder so the project produces an assembly)
- Modify: `src/ServiceConnect.slnx`
- Modify: `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` (add ProjectReference)

- [ ] **Step 1: Create the directory and csproj**

Create `src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
  </ItemGroup>

</Project>
```

This inherits `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` from `src/Directory.Build.props`.

- [ ] **Step 2: Create the nuspec**

Create `src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.nuspec`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>ServiceConnect.HealthChecks</id>
    <version>7.0.0</version>
    <title>ServiceConnect.HealthChecks</title>
    <authors>Jakub Pachansky,Tim Watson</authors>
    <owners>Jakub Pachansky,Tim Watson</owners>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <license type="expression">MIT</license>
    <description>Health-check integrations for ServiceConnect. Three opt-in IHealthCheck classes for Microsoft.Extensions.Diagnostics.HealthChecks: bus liveness (IBus.IsConsuming), consumer connection (IConsumer.IsConnected), producer connection (IProducer.IsHealthy). Transport-agnostic.</description>
    <language>en-GB</language>
    <projectUrl>https://github.com/R-Suite/ServiceConnect-CSharp</projectUrl>
    <copyright>Copyright 2026 ServiceConnect. All rights reserved</copyright>
    <tags>ServiceConnect,HealthChecks,Liveness,Readiness,MessageBus,Messaging,Message,Bus,Service</tags>
    <dependencies>
      <group targetFramework="net8.0">
        <dependency id="ServiceConnect.Interfaces" version="7.0.0" />
        <dependency id="Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions" version="9.0.0" />
      </group>
      <group targetFramework="net10.0">
        <dependency id="ServiceConnect.Interfaces" version="7.0.0" />
        <dependency id="Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions" version="9.0.0" />
      </group>
    </dependencies>
  </metadata>
  <files>
    <file src="bin\Release\net8.0\ServiceConnect.HealthChecks.dll" target="lib\net8.0" />
    <file src="bin\Release\net8.0\ServiceConnect.HealthChecks.pdb" target="lib\net8.0" />
    <file src="bin\Release\net8.0\ServiceConnect.HealthChecks.xml" target="lib\net8.0" />
    <file src="bin\Release\net10.0\ServiceConnect.HealthChecks.dll" target="lib\net10.0" />
    <file src="bin\Release\net10.0\ServiceConnect.HealthChecks.pdb" target="lib\net10.0" />
    <file src="bin\Release\net10.0\ServiceConnect.HealthChecks.xml" target="lib\net10.0" />
  </files>
</package>
```

- [ ] **Step 3: Create a placeholder source file so the assembly compiles**

Create `src/ServiceConnect.HealthChecks/AssemblyMarker.cs`:

```csharp
namespace ServiceConnect.HealthChecks;

// Marker type. Real public surface lands in subsequent tasks.
internal static class AssemblyMarker;
```

This file is **deleted** in Task 3 once `BusConsumingHealthCheck` arrives. Its only role is to give the assembly something to compile in this task so the build, slnx wiring, and pack output can be verified independently.

- [ ] **Step 4: Add the project to `src/ServiceConnect.slnx`**

Edit `src/ServiceConnect.slnx` and add a `<Project>` line for the new project, in alphabetical order with the other top-level projects. After the change, the file reads:

```xml
<Solution>
  <Configurations>
    <Platform Name="Any CPU" />
  </Configurations>
  <Folder Name="/Clients/">
    <Project Path="ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" />
  </Folder>
  <Folder Name="/Filters/">
    <Project Path="ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj" />
  </Folder>
  <Folder Name="/Persistence/">
    <Project Path="ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj" />
    <Project Path="ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj" />
  </Folder>
  <Folder Name="/Tests/">
    <Project Path="ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj" />
    <Project Path="ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" />
  </Folder>
  <Project Path="ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj" />
  <Project Path="ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj" />
  <Project Path="ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj" />
  <Project Path="ServiceConnect/ServiceConnect.csproj" />
</Solution>
```

- [ ] **Step 5: Add the project reference to the unit-test project**

Edit `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`. In the `<ItemGroup>` containing `<ProjectReference>` entries, add (alphabetically among the other ServiceConnect references):

```xml
        <ProjectReference Include="..\ServiceConnect.HealthChecks\ServiceConnect.HealthChecks.csproj" />
```

The full ItemGroup becomes:

```xml
    <ItemGroup>
        <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
        <ProjectReference Include="..\ServiceConnect\ServiceConnect.csproj" />
        <ProjectReference Include="..\ServiceConnect.Client.RabbitMQ\ServiceConnect.Client.RabbitMQ.csproj" />
        <ProjectReference Include="..\ServiceConnect.HealthChecks\ServiceConnect.HealthChecks.csproj" />
        <ProjectReference Include="..\ServiceConnect.Persistence.InMemory\ServiceConnect.Persistence.InMemory.csproj" />
        <ProjectReference Include="..\ServiceConnect.Persistence.MongoDb\ServiceConnect.Persistence.MongoDb.csproj" />
        <ProjectReference Include="..\ServiceConnect.Telemetry\ServiceConnect.Telemetry.csproj" />
        <ProjectReference Include="..\ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication.csproj" />
    </ItemGroup>
```

- [ ] **Step 6: Build the new project to verify it compiles**

Run: `dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -c Release`

Expected: build succeeds, both target frameworks (`net8.0`, `net10.0`) produce `ServiceConnect.HealthChecks.dll` under `bin/Release/`.

- [ ] **Step 7: Build the unit-test project to verify the reference resolves**

Run: `dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`

Expected: build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.HealthChecks/ \
        src/ServiceConnect.slnx \
        src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
git commit -m "feat(healthchecks): add empty ServiceConnect.HealthChecks project

Project skeleton — csproj, nuspec, slnx wiring, unit-test reference. The
real public surface (BusConsumingHealthCheck, ConsumerConnectionHealthCheck,
ProducerConnectionHealthCheck, and their AddServiceConnect* extensions)
lands in subsequent commits.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `BusConsumingHealthCheck` and `AddServiceConnectBus`

The bus liveness check and its registration extension. This is the smallest of the three checks (no transport involvement) and locks in the file-shape and test-shape patterns the other two will reuse.

**Files:**
- Delete: `src/ServiceConnect.HealthChecks/AssemblyMarker.cs`
- Create: `src/ServiceConnect.HealthChecks/BusConsumingHealthCheck.cs`
- Create: `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`
- Test: `src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs` (create — folder is new)
- Test: `src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectBusTests.cs` (create)

- [ ] **Step 1: Write the failing tests for the check class**

Create `src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class BusConsumingHealthCheckTests
{
    private readonly Mock<IBus> _bus = new();

    private BusConsumingHealthCheck CreateSut() => new(_bus.Object);

    [Fact]
    public async Task CheckHealthAsync_BusIsConsuming_ReturnsHealthy()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(true);

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("consuming", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_BusIsNotConsuming_ReturnsUnhealthy()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not consuming", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_BusIsNotConsuming_HonoursDegradedFailureStatus()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Degraded, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }
}
```

- [ ] **Step 2: Write the failing tests for the registration extension**

Create `src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectBusTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class AddServiceConnectBusTests
{
    private static HealthCheckRegistration GetSingleRegistration(IServiceCollection services)
    {
        services.AddSingleton(new Mock<IBus>().Object);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }

    [Fact]
    public void AddServiceConnectBus_DefaultName_IsServiceConnectBus()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus();

        var registration = GetSingleRegistration(services);
        Assert.Equal("serviceconnect-bus", registration.Name);
    }

    [Fact]
    public void AddServiceConnectBus_CustomName_Propagates()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus(name: "custom");

        var registration = GetSingleRegistration(services);
        Assert.Equal("custom", registration.Name);
    }

    [Fact]
    public void AddServiceConnectBus_TagsAndTimeoutAndFailureStatus_Propagate()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus(
            tags: new[] { "live" },
            timeout: TimeSpan.FromSeconds(2),
            failureStatus: HealthStatus.Degraded);

        var registration = GetSingleRegistration(services);
        Assert.Contains("live", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    [Fact]
    public void AddServiceConnectBus_RegistrationFactory_ResolvesBusConsumingHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IBus>().Object);
        services.AddHealthChecks().AddServiceConnectBus();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations);

        var instance = registration.Factory(provider);
        Assert.IsType<BusConsumingHealthCheck>(instance);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~ServiceConnect.UnitTests.HealthChecks -v minimal`

Expected: BUILD FAILURE — `BusConsumingHealthCheck` and the `AddServiceConnectBus` extension method do not exist.

- [ ] **Step 4: Delete the placeholder marker file**

```bash
rm src/ServiceConnect.HealthChecks/AssemblyMarker.cs
```

- [ ] **Step 5: Implement `BusConsumingHealthCheck`**

Create `src/ServiceConnect.HealthChecks/BusConsumingHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IBus.IsConsuming"/> is <see langword="true"/>.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
public sealed class BusConsumingHealthCheck : IHealthCheck
{
    private readonly IBus _bus;

    /// <summary>
    /// Creates a check that observes the supplied <see cref="IBus"/>.
    /// </summary>
    public BusConsumingHealthCheck(IBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_bus.IsConsuming)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Bus is consuming."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Bus is not consuming."));
    }
}
```

- [ ] **Step 6: Implement the `AddServiceConnectBus` extension**

Create `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Extension methods on <see cref="IHealthChecksBuilder"/> for registering
/// ServiceConnect health checks. Each method registers exactly one check.
/// Pick the methods that match what your host actually does — a publish-only
/// host should not register the consumer check, a consume-only host should
/// not register the producer check.
/// </summary>
public static class HealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers a health check that reports Healthy when the bus is consuming
    /// (<see cref="ServiceConnect.Interfaces.IBus.IsConsuming"/>).
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectBus(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-bus",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => ActivatorUtilities.CreateInstance<BusConsumingHealthCheck>(sp),
            failureStatus,
            tags,
            timeout));
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~ServiceConnect.UnitTests.HealthChecks -v minimal`

Expected: 7 tests pass (3 `BusConsumingHealthCheckTests` + 4 `AddServiceConnectBusTests`).

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.HealthChecks/ \
        src/ServiceConnect.UnitTests/HealthChecks/
git commit -m "feat(healthchecks): add BusConsumingHealthCheck and AddServiceConnectBus

Surfaces IBus.IsConsuming through a Microsoft.Extensions.Diagnostics
IHealthCheck. The extension method registers exactly one check, defaults
to name 'serviceconnect-bus', and propagates failureStatus/tags/timeout
verbatim.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `ConsumerConnectionHealthCheck` and `AddServiceConnectConsumer`

Same shape as Task 3, against `IConsumer.IsConnected`.

**Files:**
- Create: `src/ServiceConnect.HealthChecks/ConsumerConnectionHealthCheck.cs`
- Modify: `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs` (add `AddServiceConnectConsumer`)
- Test: `src/ServiceConnect.UnitTests/HealthChecks/ConsumerConnectionHealthCheckTests.cs` (create)
- Test: `src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectConsumerTests.cs` (create)

- [ ] **Step 1: Write the failing check tests**

Create `src/ServiceConnect.UnitTests/HealthChecks/ConsumerConnectionHealthCheckTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ConsumerConnectionHealthCheckTests
{
    private readonly Mock<IConsumer> _consumer = new();

    private ConsumerConnectionHealthCheck CreateSut() => new(_consumer.Object);

    [Fact]
    public async Task CheckHealthAsync_ConsumerConnected_ReturnsHealthy()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(true);

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("open", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ConsumerNotConnected_ReturnsUnhealthy()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("closed", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ConsumerNotConnected_HonoursDegradedFailureStatus()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Degraded, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }
}
```

- [ ] **Step 2: Write the failing extension tests**

Create `src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectConsumerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class AddServiceConnectConsumerTests
{
    private static HealthCheckRegistration GetSingleRegistration(IServiceCollection services)
    {
        services.AddSingleton(new Mock<IConsumer>().Object);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }

    [Fact]
    public void AddServiceConnectConsumer_DefaultName_IsServiceConnectConsumer()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectConsumer();

        var registration = GetSingleRegistration(services);
        Assert.Equal("serviceconnect-consumer", registration.Name);
    }

    [Fact]
    public void AddServiceConnectConsumer_CustomName_Propagates()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectConsumer(name: "custom");

        var registration = GetSingleRegistration(services);
        Assert.Equal("custom", registration.Name);
    }

    [Fact]
    public void AddServiceConnectConsumer_TagsAndTimeoutAndFailureStatus_Propagate()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectConsumer(
            tags: new[] { "ready" },
            timeout: TimeSpan.FromSeconds(2),
            failureStatus: HealthStatus.Degraded);

        var registration = GetSingleRegistration(services);
        Assert.Contains("ready", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    [Fact]
    public void AddServiceConnectConsumer_RegistrationFactory_ResolvesConsumerConnectionHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IConsumer>().Object);
        services.AddHealthChecks().AddServiceConnectConsumer();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations);

        var instance = registration.Factory(provider);
        Assert.IsType<ConsumerConnectionHealthCheck>(instance);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumerConnectionHealthCheckTests|FullyQualifiedName~AddServiceConnectConsumerTests" -v minimal`

Expected: BUILD FAILURE — `ConsumerConnectionHealthCheck` and `AddServiceConnectConsumer` do not exist.

- [ ] **Step 4: Implement `ConsumerConnectionHealthCheck`**

Create `src/ServiceConnect.HealthChecks/ConsumerConnectionHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IConsumer.IsConnected"/> is <see langword="true"/>.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
public sealed class ConsumerConnectionHealthCheck : IHealthCheck
{
    private readonly IConsumer _consumer;

    /// <summary>
    /// Creates a check that observes the supplied <see cref="IConsumer"/>.
    /// </summary>
    public ConsumerConnectionHealthCheck(IConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _consumer = consumer;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_consumer.IsConnected)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Consumer connection is open."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Consumer connection is closed."));
    }
}
```

- [ ] **Step 5: Add `AddServiceConnectConsumer` to the existing extensions class**

Edit `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`. Add a new method below the existing `AddServiceConnectBus` (inside the same `HealthChecksBuilderExtensions` class, before the closing brace):

```csharp
    /// <summary>
    /// Registers a health check that reports Healthy when the consumer connection
    /// is open (<see cref="ServiceConnect.Interfaces.IConsumer.IsConnected"/>).
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectConsumer(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-consumer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => ActivatorUtilities.CreateInstance<ConsumerConnectionHealthCheck>(sp),
            failureStatus,
            tags,
            timeout));
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumerConnectionHealthCheckTests|FullyQualifiedName~AddServiceConnectConsumerTests" -v minimal`

Expected: 7 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.HealthChecks/ConsumerConnectionHealthCheck.cs \
        src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs \
        src/ServiceConnect.UnitTests/HealthChecks/ConsumerConnectionHealthCheckTests.cs \
        src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectConsumerTests.cs
git commit -m "feat(healthchecks): add ConsumerConnectionHealthCheck and AddServiceConnectConsumer

Surfaces IConsumer.IsConnected through an IHealthCheck. Same registration
shape as the bus check; defaults to name 'serviceconnect-consumer'.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: `ProducerConnectionHealthCheck` and `AddServiceConnectProducer`

Same shape as Tasks 3 and 4, against `IProducer.IsHealthy` (added in Task 1).

**Files:**
- Create: `src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs`
- Modify: `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs` (add `AddServiceConnectProducer`)
- Test: `src/ServiceConnect.UnitTests/HealthChecks/ProducerConnectionHealthCheckTests.cs` (create)
- Test: `src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectProducerTests.cs` (create)

- [ ] **Step 1: Write the failing check tests**

Create `src/ServiceConnect.UnitTests/HealthChecks/ProducerConnectionHealthCheckTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ProducerConnectionHealthCheckTests
{
    private readonly Mock<IProducer> _producer = new();

    private ProducerConnectionHealthCheck CreateSut() => new(_producer.Object);

    [Fact]
    public async Task CheckHealthAsync_ProducerHealthy_ReturnsHealthy()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(true);

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("open", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ProducerNotHealthy_ReturnsUnhealthy()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("closed", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ProducerNotHealthy_HonoursDegradedFailureStatus()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Degraded, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }
}
```

- [ ] **Step 2: Write the failing extension tests**

Create `src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectProducerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class AddServiceConnectProducerTests
{
    private static HealthCheckRegistration GetSingleRegistration(IServiceCollection services)
    {
        services.AddSingleton(new Mock<IProducer>().Object);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }

    [Fact]
    public void AddServiceConnectProducer_DefaultName_IsServiceConnectProducer()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectProducer();

        var registration = GetSingleRegistration(services);
        Assert.Equal("serviceconnect-producer", registration.Name);
    }

    [Fact]
    public void AddServiceConnectProducer_CustomName_Propagates()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectProducer(name: "custom");

        var registration = GetSingleRegistration(services);
        Assert.Equal("custom", registration.Name);
    }

    [Fact]
    public void AddServiceConnectProducer_TagsAndTimeoutAndFailureStatus_Propagate()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectProducer(
            tags: new[] { "ready" },
            timeout: TimeSpan.FromSeconds(2),
            failureStatus: HealthStatus.Degraded);

        var registration = GetSingleRegistration(services);
        Assert.Contains("ready", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    [Fact]
    public void AddServiceConnectProducer_RegistrationFactory_ResolvesProducerConnectionHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IProducer>().Object);
        services.AddHealthChecks().AddServiceConnectProducer();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations);

        var instance = registration.Factory(provider);
        Assert.IsType<ProducerConnectionHealthCheck>(instance);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerConnectionHealthCheckTests|FullyQualifiedName~AddServiceConnectProducerTests" -v minimal`

Expected: BUILD FAILURE — `ProducerConnectionHealthCheck` and `AddServiceConnectProducer` do not exist.

- [ ] **Step 4: Implement `ProducerConnectionHealthCheck`**

Create `src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IProducer.IsHealthy"/> is <see langword="true"/>.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// The producer connects lazily on the first publish/send call, so this check
/// reports Unhealthy until the host has published at least once. Hosts that do
/// not publish at startup should not register this check on a readiness tag.
/// </remarks>
public sealed class ProducerConnectionHealthCheck : IHealthCheck
{
    private readonly IProducer _producer;

    /// <summary>
    /// Creates a check that observes the supplied <see cref="IProducer"/>.
    /// </summary>
    public ProducerConnectionHealthCheck(IProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        _producer = producer;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_producer.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Producer connection is open."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Producer connection is closed."));
    }
}
```

- [ ] **Step 5: Add `AddServiceConnectProducer` to the extensions class**

Edit `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`. Add below `AddServiceConnectConsumer`:

```csharp
    /// <summary>
    /// Registers a health check that reports Healthy when the producer connection
    /// is open (<see cref="ServiceConnect.Interfaces.IProducer.IsHealthy"/>).
    /// </summary>
    /// <remarks>
    /// The producer connects lazily on the first publish/send call. Hosts that
    /// do not publish at startup should not register this check on a readiness
    /// tag — it would report Unhealthy until the first outbound message.
    /// </remarks>
    public static IHealthChecksBuilder AddServiceConnectProducer(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-producer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => ActivatorUtilities.CreateInstance<ProducerConnectionHealthCheck>(sp),
            failureStatus,
            tags,
            timeout));
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~ServiceConnect.UnitTests.HealthChecks -v minimal`

Expected: 21 tests pass total (7 from each of Tasks 3, 4, 5).

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs \
        src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs \
        src/ServiceConnect.UnitTests/HealthChecks/ProducerConnectionHealthCheckTests.cs \
        src/ServiceConnect.UnitTests/HealthChecks/AddServiceConnectProducerTests.cs
git commit -m "feat(healthchecks): add ProducerConnectionHealthCheck and AddServiceConnectProducer

Surfaces IProducer.IsHealthy through an IHealthCheck. Documents the
'producer connects lazily on first publish' caveat on both the check and
the extension method, since hosts that do not publish at startup should
not register this check on a readiness tag.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: End-to-end wiring test (positive path)

One integration test that exercises the full DI graph against the existing Testcontainers RabbitMQ. The negative path (Unhealthy when the underlying property is false) is fully covered by the unit tests; this test verifies wiring — `AddServiceConnect` + `UseRabbitMQ` + `AddHealthChecks().AddServiceConnect{Bus,Consumer,Producer}()` resolve correctly through `HealthCheckService` and report Healthy when everything is alive.

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/HealthChecks/HealthCheckEndToEndTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj` (add ProjectReference to HealthChecks)

- [ ] **Step 1: Inspect the EndToEndTests csproj and an existing test for the host-build pattern**

Run: `cat src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj`

Then run: `cat src/ServiceConnect.EndToEndTests/Bus/AutoStartConsumingE2ETests.cs | head -80`

The point is to copy the existing test's DI setup, fixture wiring, and configuration so the new test follows the project's conventions exactly. If the existing pattern uses `MessagingFixture` and `IClassFixture<MessagingFixture>`, follow that. If it builds a `Microsoft.Extensions.Hosting.Host` rather than a `ServiceCollection`, follow that. **Do not invent a parallel pattern.**

- [ ] **Step 2: Add the project reference**

Edit `src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj` and add to the `<ItemGroup>` of project references:

```xml
        <ProjectReference Include="..\ServiceConnect.HealthChecks\ServiceConnect.HealthChecks.csproj" />
```

- [ ] **Step 3: Write the integration test**

Create `src/ServiceConnect.EndToEndTests/HealthChecks/HealthCheckEndToEndTests.cs`. The test must:

1. Use `MessagingFixture` (same pattern as `AutoStartConsumingE2ETests`).
2. Build a `ServiceCollection` (or `Host`, matching whatever the existing E2E pattern is) that calls `services.AddServiceConnect(b => b.UseRabbitMQ(...))` against the fixture's RabbitMQ instance, then `services.AddHealthChecks().AddServiceConnectBus().AddServiceConnectConsumer().AddServiceConnectProducer()`.
3. Start the bus (so `IsConsuming` becomes true and the consumer connection opens).
4. Publish at least one message via `IBus.Publish(...)` so the producer's lazy connect runs and `IsHealthy` becomes true.
5. Resolve `HealthCheckService` from the service provider and call `CheckHealthAsync()`.
6. Assert: overall report status is `Healthy`; report contains exactly three entries named `serviceconnect-bus`, `serviceconnect-consumer`, `serviceconnect-producer`; each entry has `HealthStatus.Healthy`.

A minimum viable test body — adapt the host-construction lines to match the project's existing E2E pattern observed in step 1:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests.HealthChecks;

[Collection("Messaging")] // or whatever the existing E2E collection convention is
public class HealthCheckEndToEndTests : IClassFixture<MessagingFixture>
{
    private readonly MessagingFixture _fixture;

    public HealthCheckEndToEndTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AllThreeChecks_ReportHealthy_WhenBusAndBrokerAreAlive()
    {
        var queueName = _fixture.GetUniqueQueueName("healthchecks");
        var services = new ServiceCollection();
        services.AddLogging();

        // FOLLOW THE EXISTING E2E PATTERN observed in step 1 for AddServiceConnect
        // and UseRabbitMQ wiring against the fixture. Example shape:
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.UseRabbitMQ(opts =>
            {
                opts.Host = _fixture.RabbitMqHostname;
                // ... port, credentials per fixture
            });
        });

        services.AddHealthChecks()
            .AddServiceConnectBus(tags: new[] { "live" })
            .AddServiceConnectConsumer(tags: new[] { "ready" })
            .AddServiceConnectProducer(tags: new[] { "ready" });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        // Trigger the producer's lazy connect.
        await bus.PublishAsync(new HealthProbeMessage { Marker = "probe" });

        var hcService = provider.GetRequiredService<HealthCheckService>();
        var report = await hcService.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(3, report.Entries.Count);
        Assert.True(report.Entries.ContainsKey("serviceconnect-bus"));
        Assert.True(report.Entries.ContainsKey("serviceconnect-consumer"));
        Assert.True(report.Entries.ContainsKey("serviceconnect-producer"));
        foreach (var entry in report.Entries)
        {
            Assert.Equal(HealthStatus.Healthy, entry.Value.Status);
        }

        await bus.StopConsumingAsync();
    }

    public sealed class HealthProbeMessage : Message
    {
        public required string Marker { get; init; }
    }
}
```

If `Message` is the wrong base type for this codebase (check by reading what other E2E tests inherit from), substitute. The exact shape of the publish call must match what the codebase actually exposes — read one nearby E2E publish call to confirm.

- [ ] **Step 4: Run the integration test**

Run: `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter FullyQualifiedName~HealthCheckEndToEndTests -v minimal`

Expected: 1 test passes. The test starts a real RabbitMQ container via Testcontainers, so the first run takes ~30–60s for image pull on a cold cache.

If the test fails because the producer's `IsHealthy` is still `false` after publish, the publish call may have completed before `EnsureConnectedAsync` set `_connected`. Add a brief retry loop around the `CheckHealthAsync` call (poll up to 5 seconds) — this is a known consequence of the lazy-connect semantics and is exactly the case the spec calls out.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/HealthChecks/ \
        src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj
git commit -m "test(healthchecks): add positive-path E2E wiring test

Verifies AddServiceConnect + UseRabbitMQ + AddHealthChecks chain resolves
through HealthCheckService and reports Healthy when bus/broker are alive.
The negative path is covered by the unit tests in the HealthChecks suite;
asserting Unhealthy under broker outage requires a broker-drop helper
that the E2E suite does not currently provide.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Documentation rewrite

Replace the "## No health checks" section in `observability.mdx` with a shipped-feature "## Health checks" section, and add a one-paragraph pointer in `hosting.mdx`.

**Files:**
- Modify: `website/src/content/docs/learn/operations/observability.mdx:136-140`
- Modify: `website/src/content/docs/learn/operations/hosting.mdx`

- [ ] **Step 1: Read `hosting.mdx` to choose the natural anchor for the pointer paragraph**

Run: `cat website/src/content/docs/learn/operations/hosting.mdx`

Look for a section heading that already covers post-startup operational concerns (probes, health, monitoring). If none exists, add the pointer paragraph at the end of the page above any "What comes next" / "See also" footer. Do not invent a new section heading.

- [ ] **Step 2: Rewrite `observability.mdx` lines 136–140**

Edit `website/src/content/docs/learn/operations/observability.mdx`. Replace the existing "## No health checks" section (lines 136–140) with the following. The first heading **must** be `## Health checks`:

```mdx
## Health checks

`ServiceConnect.HealthChecks` ships three opt-in `IHealthCheck` classes for `Microsoft.Extensions.Diagnostics.HealthChecks`:

| Check | Default name | Observes |
|---|---|---|
| `BusConsumingHealthCheck` | `serviceconnect-bus` | `IBus.IsConsuming` |
| `ConsumerConnectionHealthCheck` | `serviceconnect-consumer` | `IConsumer.IsConnected` |
| `ProducerConnectionHealthCheck` | `serviceconnect-producer` | `IProducer.IsHealthy` |

The package is transport-agnostic — it depends only on `ServiceConnect.Interfaces`. All three checks are O(1) state inspections; they do not open broker channels or perform AMQP round-trips per probe.

```bash
dotnet add package ServiceConnect.HealthChecks
```

### Wiring

Pick the calls that match what your host actually does. A consume-only host omits `AddServiceConnectProducer`; a publish-only host omits `AddServiceConnectConsumer`; hosts that do both call all three.

```csharp
using ServiceConnect.HealthChecks;

services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(opts => opts.Host = "rabbit");
});

services.AddHealthChecks()
    .AddServiceConnectBus(tags: new[] { "live" })
    .AddServiceConnectConsumer(tags: new[] { "ready" })
    .AddServiceConnectProducer(tags: new[] { "ready" });

app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
```

### Steady-state semantics

Each check inspects the *last known* state from the transport client's event stream. There is no active probing — that would compete with real traffic and amplify failure modes (a probe interval of 5 seconds × N replicas would mean steady channel churn against the broker for what is, in practice, a one-bit signal).

Two timing footnotes worth knowing:

- **The producer connects lazily.** `IProducer.IsHealthy` is `false` before the host has published or sent its first message. For hosts that publish anything at startup (request/reply, heartbeats, subscribe-confirm), this resolves within milliseconds; for hosts that publish only in response to inbound traffic, the producer check is Unhealthy until the first outbound message. If your host falls in the second category, **do not put the producer check on the `ready` tag** — it would block readiness until the first inbound message arrives. Use `AddServiceConnectConsumer` only for that topology.
- **The window between connection drop and event observation.** When the broker connection drops, there is a small (millisecond-scale) gap before the client raises its shutdown event and `IsConnected` / `IsHealthy` flip to `false`. A probe firing inside that gap can still see Healthy. This is shorter than any K8s probe interval and is the same gap any in-process check has, regardless of implementation.

### Custom checks

If you need anything more than the shipped three checks — a custom predicate over multiple bus state pieces, a different failure-status mapping, integration with a non-`Microsoft.Extensions.Diagnostics.HealthChecks` framework — implement `IHealthCheck` directly against `IBus`, `IConsumer`, or `IProducer`. The shipped check classes are sealed; their source is short enough to copy as a starting point.
```

- [ ] **Step 3: Add the pointer in `hosting.mdx`**

Edit `website/src/content/docs/learn/operations/hosting.mdx`. At the natural anchor identified in step 1 (or at the end of the page if none fits), add:

```mdx
## Health checks

The `ServiceConnect.HealthChecks` package ships `IHealthCheck` classes for bus, consumer, and producer state, registered through `IHealthChecksBuilder` extensions on `services.AddHealthChecks()`. See [Observability — Health checks](/ServiceConnect-CSharp/learn/operations/observability/#health-checks) for the wiring details.
```

If the page already has its own "What comes next" section, add this above it; otherwise place it as the new final section.

- [ ] **Step 4: Verify the website builds**

Run: `cd website && npm run build`

Expected: build succeeds, no broken-link warnings for the new anchor `#health-checks`.

(If the website tooling is not Node — check `website/package.json` first. The repo's existing pattern is the build command in `website/`'s README; follow that.)

- [ ] **Step 5: Commit**

```bash
git add website/src/content/docs/learn/operations/observability.mdx \
        website/src/content/docs/learn/operations/hosting.mdx
git commit -m "docs: rewrite observability health-checks section for shipped reality

The 'No health checks' section is now a 'Health checks' section: three
opt-in IHealthCheck classes, transport-agnostic, with steady-state
semantics and the producer-lazy-connect caveat documented. Adds a
pointer paragraph from hosting.mdx.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Verify packaging

Confirm `dotnet pack` produces a `.nupkg` whose dependency closure matches the spec — abstractions package and `ServiceConnect.Interfaces` only, no transitive bus/transport deps.

**Files:** none (verification only)

- [ ] **Step 1: Pack the new project in Release**

Run: `dotnet pack src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -c Release`

Expected: a `ServiceConnect.HealthChecks.7.0.0.nupkg` is produced under `src/ServiceConnect.HealthChecks/bin/Release/` (path may vary slightly based on whether the nuspec or csproj drives packaging — check both).

- [ ] **Step 2: Inspect the .nuspec inside the produced .nupkg**

Run: `unzip -p src/ServiceConnect.HealthChecks/bin/Release/ServiceConnect.HealthChecks.7.0.0.nupkg ServiceConnect.HealthChecks.nuspec`

Expected dependencies (per `<group>`, for both `net8.0` and `net10.0`):
- `ServiceConnect.Interfaces` 7.0.0
- `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` 9.0.0

**Critical:** there must be **no** dependency on `ServiceConnect`, `ServiceConnect.Client.RabbitMQ`, or any transport package. If any of those are listed, the project's `<ProjectReference>` set is wrong — go back to Task 2 and remove the offending reference.

- [ ] **Step 3: Inspect the assembly contents**

Run: `unzip -l src/ServiceConnect.HealthChecks/bin/Release/ServiceConnect.HealthChecks.7.0.0.nupkg | grep -E "lib/(net8.0|net10.0)/"`

Expected: `ServiceConnect.HealthChecks.dll`, `ServiceConnect.HealthChecks.pdb`, `ServiceConnect.HealthChecks.xml` — for both `net8.0` and `net10.0`.

- [ ] **Step 4: Run the full unit-test suite as a final regression check**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v minimal`

Expected: full suite passes — every existing test plus the 21 new `HealthChecks/` tests plus the 3 new `RabbitMQ/ProducerIsHealthyTests` tests.

- [ ] **Step 5: Run the E2E test once more**

Run: `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter FullyQualifiedName~HealthCheckEndToEndTests -v minimal`

Expected: `HealthCheckEndToEndTests` passes.

- [ ] **Step 6: No commit needed**

Verification only — there are no code changes from this task. If any of the verification steps fail, treat the failure as a regression in earlier tasks and fix it there with a follow-up commit, not by modifying this verification task.

---

## Acceptance criteria

The implementation is complete when:

- All eight tasks are committed.
- `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` passes (full existing suite + 24 new tests: 3 from Task 1, 21 from Tasks 3–5).
- `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter FullyQualifiedName~HealthCheckEndToEndTests` passes.
- `dotnet pack src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -c Release` produces a `.nupkg` whose only dependencies are `ServiceConnect.Interfaces` and `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`.
- `website/src/content/docs/learn/operations/observability.mdx` no longer contains a "No health checks" section; it has a "Health checks" section that documents the three checks, their wiring, and the producer-lazy-connect caveat.
- The website build succeeds with no broken-link warnings.
