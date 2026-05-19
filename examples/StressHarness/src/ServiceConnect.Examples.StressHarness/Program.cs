using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Cli;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Orchestrator;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using ServiceConnect.Examples.StressHarness.Reporting;
using ServiceConnect.Interfaces;

try
{
    var opts = HarnessCliParser.Parse(args);
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

    var harnessOptions = new HarnessOptions(
        BrokerUri: opts.BrokerUri,
        PersistenceMode: opts.Persistence,
        MongoConnectionString: opts.Persistence == "mongo" ? "mongodb://localhost:27017" : null,
        FlowTimeout: opts.FlowTimeout,
        MemoryBudgetBytes: opts.MemoryBudgetBytes,
        ReportDir: opts.ReportDir);

    var accounting = new FlowAccounting();
    var signals = new PerHandlerSignal();
    var workItemCounters = new WorkItemCounters();

    IReadOnlyList<IPatternDriver> drivers =
    [
        new PointToPointDriver(accounting, signals),
        new PublishSubscribeDriver(accounting, signals),
        new RequestReplyDriver(accounting, signals),
        new CompetingConsumersDriver(accounting, signals, workItemCounters),
    ];

    // Composite handler-reference list spans every pattern driver wired up below.
    // HarnessHost pre-registers an empty IReadOnlyList<HandlerReference> before
    // AddServiceConnect runs (so the framework's TryAddSingleton inside RegisterHandlers
    // is a no-op); the per-bus callback below Replaces that empty list with this one.
    // MessageTypeRegistry is registered via a factory that resolves IReadOnlyList<HandlerReference>
    // lazily, so the swap is observed at first registry build.
    var handlerReferences = new List<HandlerReference>
    {
        new() { MessageType = typeof(P2pPing), HandlerType = typeof(P2pHandler) },
        new() { MessageType = typeof(PubSubEvent), HandlerType = typeof(PubSubHandler) },
        new() { MessageType = typeof(QuoteRequest), HandlerType = typeof(QuoteRequestHandler) },
        new() { MessageType = typeof(WorkItem), HandlerType = typeof(WorkItemHandler) },
    };

    await using var host = await HarnessHost.StartAsync(
        harnessOptions,
        registerPerBus: (builder, busTag) =>
        {
            builder.AddRegistration(services =>
            {
                // Replace the HarnessHost-supplied empty handler-reference list with
                // the driver-composed list so MessageTypeRegistry recognises P2pPing
                // at dispatch (otherwise the dispatcher rejects the inbound message
                // as Unregistered and routes it as not-handled).
                services.Replace(ServiceDescriptor.Singleton<IReadOnlyList<HandlerReference>>(handlerReferences));

                // Process-wide singletons for the harness orchestration. Both buses
                // share the same instances so the driver's await and the receiving
                // bus's signal land on the same accounting and rendezvous registry.
                services.TryAddSingleton(accounting);
                services.TryAddSingleton(signals);
                services.TryAddSingleton(workItemCounters);

                // Factory captures busTag from the registerPerBus closure so the same
                // handler class produces an alpha-tagged instance on the alpha bus and
                // a beta-tagged instance on the beta bus without inspecting headers.
                // Transient lifetime matches the framework's own handler registration
                // contract (handlers may not be singletons — HandlerProcessor resolves
                // them per dispatched message via GetServices).
                services.AddTransient<IMessageHandler<P2pPing>>(sp => new P2pHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));

                services.AddTransient<IMessageHandler<PubSubEvent>>(sp => new PubSubHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));

                services.AddTransient<IMessageHandler<QuoteRequest>>(sp => new QuoteRequestHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));

                // Two WorkItemHandler registrations per bus, distinguished by their
                // handler tag. GetServices(IMessageHandler<WorkItem>) returns both, so
                // each delivery fans out across the pair; the competing-consumers driver
                // asserts that more than one handler bumped its counter. The handler tags
                // are closure-captured constants rather than configuration so the count of
                // distinct workers per bus stays at exactly two — the assertion's lower
                // bound is meaningful only when the registration count is known.
                services.AddTransient<IMessageHandler<WorkItem>>(sp => new WorkItemHandler(
                    handlerTag: "h1",
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>(),
                    sp.GetRequiredService<WorkItemCounters>()));

                services.AddTransient<IMessageHandler<WorkItem>>(sp => new WorkItemHandler(
                    handlerTag: "h2",
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>(),
                    sp.GetRequiredService<WorkItemCounters>()));
            });
        },
        loggerFactory,
        CancellationToken.None);

    var console = new ConsoleReporter();
    var dispatcher = new ModeDispatcher(
        opts,
        drivers,
        host,
        accounting,
        console,
        loggerFactory.CreateLogger<ModeDispatcher>());

    var report = await dispatcher.RunAsync(CancellationToken.None);

    Directory.CreateDirectory(opts.ReportDir);
    await JsonReportWriter.WriteAsync(report, Path.Combine(opts.ReportDir, "report.json"), CancellationToken.None);
    await MarkdownReportWriter.WriteAsync(report, Path.Combine(opts.ReportDir, "report.md"), CancellationToken.None);

    console.Summary(report);

    return report.FailedFlows == 0 && report.ProcessAssertionFailures.Count == 0 ? 0 : 1;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (NotSupportedException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
