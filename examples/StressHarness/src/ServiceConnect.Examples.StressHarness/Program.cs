using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Cli;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Orchestrator;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Patterns.Aggregators;
using ServiceConnect.Examples.StressHarness.Patterns.Filters;
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
    var filterTrail = new FilterTrail();
    var sagaObservations = new SagaObservations();
    var aggregatorObservations = new AggregatorObservations();

    IReadOnlyList<IPatternDriver> drivers =
    [
        new PointToPointDriver(accounting, signals),
        new PublishSubscribeDriver(accounting, signals),
        new RequestReplyDriver(accounting, signals),
        new CompetingConsumersDriver(accounting, signals, workItemCounters),
        new ContentBasedRoutingDriver(accounting, signals),
        new PolymorphicMessagesDriver(accounting, signals),
        new FiltersDriver(accounting, signals, filterTrail),
        new ProcessManagerDriver(accounting, signals, sagaObservations),
        new AggregatorDriver(accounting, aggregatorObservations),
        new ScatterGatherDriver(accounting, signals),
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
        new() { MessageType = typeof(PremiumOrder), HandlerType = typeof(PremiumOrderHandler) },
        new() { MessageType = typeof(StandardOrder), HandlerType = typeof(StandardOrderHandler) },
        // DomainEvent ref drives dispatch — the base-type entry instructs the registry
        // to build a descriptor for IMessageHandler<DomainEvent>, which the hierarchy
        // walk in HandlerProcessor reaches when a concrete derived event arrives. The
        // two concrete entries (OrderPlacedEvent / OrderShippedEvent) exist so the
        // bus binds the receiver queue to each concrete type's fanout exchange; without
        // them the published deliveries would never reach the queue, because the
        // DomainEvent exchange is never published to (the type is abstract).
        new() { MessageType = typeof(DomainEvent), HandlerType = typeof(DomainEventHandler) },
        new() { MessageType = typeof(OrderPlacedEvent), HandlerType = typeof(DomainEventHandler) },
        new() { MessageType = typeof(OrderShippedEvent), HandlerType = typeof(DomainEventHandler) },
        new() { MessageType = typeof(FilteredMessage), HandlerType = typeof(FilteredMessageHandler) },
        new() { MessageType = typeof(SagaStarted), HandlerType = typeof(SagaHandler) },
        new() { MessageType = typeof(SagaIntermediate), HandlerType = typeof(SagaHandler) },
        new() { MessageType = typeof(SagaCompleted), HandlerType = typeof(SagaHandler) },
        new() { MessageType = typeof(TelemetrySlice), HandlerType = typeof(StressTelemetrySliceAggregator) },
        new() { MessageType = typeof(SearchRequest), HandlerType = typeof(SearchRequestHandler) },
    };

    await using var host = await HarnessHost.StartAsync(
        harnessOptions,
        registerPerBus: (builder, busTag) =>
        {
            // Filter registration must be on the builder (it appends to the bus's
            // pipeline configuration), not inside AddRegistration. The DI factory for
            // the filter itself lives below alongside the handler registrations so
            // the framework's IServiceProvider.GetRequiredService<StressTrailFilter>
            // call at dispatch time resolves to an instance closing over the shared
            // FilterTrail singleton.
            builder.AddBeforeConsumingFilter<StressTrailFilter>();

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
                services.TryAddSingleton(filterTrail);
                services.TryAddSingleton(sagaObservations);
                services.TryAddSingleton(aggregatorObservations);

                // Filter is resolved per dispatch via GetRequiredService; transient
                // lifetime matches its observational role (no state held on the filter
                // itself, all state lives on the shared FilterTrail singleton).
                services.AddTransient<StressTrailFilter>(sp => new StressTrailFilter(
                    sp.GetRequiredService<FilterTrail>()));

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

                services.AddTransient<IMessageHandler<PremiumOrder>>(sp => new PremiumOrderHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));

                services.AddTransient<IMessageHandler<StandardOrder>>(sp => new StandardOrderHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));

                services.AddTransient<IMessageHandler<DomainEvent>>(sp => new DomainEventHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));

                services.AddTransient<IMessageHandler<FilteredMessage>>(sp => new FilteredMessageHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>(),
                    sp.GetRequiredService<FilterTrail>()));

                // SagaHandler implements three IProcessHandler<SagaData, *> interfaces; each
                // must be registered separately so the framework's per-message-type resolution
                // (GetRequiredService<IProcessHandler<SagaData, SagaStarted>>) finds the
                // matching instance. All three registrations resolve to fresh handler
                // instances that close over the same shared singletons — the per-call
                // factory makes the busTag visible to the handler without inspecting headers.
                services.AddTransient<IProcessHandler<SagaData, SagaStarted>>(sp => new SagaHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>(),
                    sp.GetRequiredService<SagaObservations>()));
                services.AddTransient<IProcessHandler<SagaData, SagaIntermediate>>(sp => new SagaHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>(),
                    sp.GetRequiredService<SagaObservations>()));
                services.AddTransient<IProcessHandler<SagaData, SagaCompleted>>(sp => new SagaHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>(),
                    sp.GetRequiredService<SagaObservations>()));

                // Aggregator is resolved by the framework via GetRequiredService<Aggregator<T>>
                // for each batch flush — not IMessageHandler<T>. The per-bus factory closes
                // over the bus tag so the observation record identifies which bus dispatched
                // the batch, matching the cross-tenant assertion shape used by the other
                // pattern drivers.
                services.AddTransient<Aggregator<TelemetrySlice>>(sp => new StressTelemetrySliceAggregator(
                    busTag,
                    sp.GetRequiredService<AggregatorObservations>()));

                // One SearchRequest handler per bus. PublishRequestAsync fans out across the
                // SearchRequest type exchange, so a publish from either bus reaches both
                // alpha's and beta's queues; each handler replies with its bus tag in
                // SearchResponse.CatalogName so the driver can prove the fanout reached both
                // subscribers. A single registration per bus keeps the reply count at the
                // ExpectedReplyCount = 2 the driver asserts against.
                services.AddTransient<IMessageHandler<SearchRequest>>(sp => new SearchRequestHandler(
                    busTag,
                    sp.GetRequiredService<FlowAccounting>(),
                    sp.GetRequiredService<PerHandlerSignal>()));
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
