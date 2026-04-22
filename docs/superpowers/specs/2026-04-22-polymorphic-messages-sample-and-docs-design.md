# Polymorphic Messages: Sample and Documentation

**Date:** 2026-04-22
**Status:** Approved
**Owner:** Tim Watson

## Context

ServiceConnect's dispatcher already supports polymorphic message handling: a handler registered for a base type receives derived-type publishes, because the dispatcher walks the message's type hierarchy when resolving handlers. An end-to-end test ([`PolymorphicMessageTests.cs`](../../../src/ServiceConnect.EndToEndTests/Routing/PolymorphicMessageTests.cs)) proves the behaviour.

The library supports it, but the docs discourage it. [`messages.mdx:78-82`](../../../website/src/content/docs/learn/core-concepts/messages.mdx) tells readers to **avoid** cross-service inheritance, and there is no runnable sample demonstrating the pattern. That gap is the problem this design closes.

The decision here is to **promote polymorphic messages as a first-class pattern** — at the same level as Pub/Sub, Content-Based Routing, and the nine other messaging patterns the project already documents. Polymorphism is the right tool for cross-cutting subscribers (audit, metrics, outbox) that care about a category of events rather than specific types.

## Goals

1. Add a runnable sample that demonstrates the pattern end-to-end: a base-type handler that catches a category of events, alongside a specific-type handler that catches one.
2. Add a pattern page to the learn docs.
3. Soften the "avoid cross-service inheritance" guidance on `messages.mdx` so it aligns with the new position.
4. Wire the new page into sidebar navigation and the samples catalog.

## Non-goals

- A helper that scans for derived types and auto-registers `HandlerReference`s. That is a real ergonomics improvement but scope creep for this change — a separate brainstorm.
- Multi-level hierarchy support in the sample. Kept single-level to match the guidance the new page will give.
- Reference documentation for polymorphic dispatch semantics. The pattern page covers dispatch adequately; no new reference page.
- Any change to `IMessageSerializer` defaults or behaviour.
- Changes to the existing E2E test — it already covers library behaviour.

## The pattern, stated plainly

Polymorphic messages let you **categorise events in code** and have one handler catch the whole category.

```csharp
public abstract class DomainEvent(Guid correlationId) : Message(correlationId)
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public sealed class OrderPlaced(Guid correlationId) : DomainEvent(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public decimal Total { get; init; }
}

public sealed class OrderShipped(Guid correlationId) : DomainEvent(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public string Carrier { get; init; } = string.Empty;
}
```

An audit subscriber handles `DomainEvent` and catches both. A shipping subscriber handles `OrderShipped` specifically. One publish of `OrderShipped` reaches both handlers.

## The subscription gotcha

[`HandlerScanner.ScanForHandlers`](../../../src/ServiceConnect/Services/HandlerScanner.cs) registers a `HandlerReference` for the **exact** generic argument on `IMessageHandler<T>`. The dispatcher walks the type hierarchy when resolving handlers at runtime, but subscription setup does not — it only binds the queue to the exchanges it has references for.

So a subscriber that declares `IMessageHandler<DomainEvent>` gets a queue bound to `DomainEvent`'s exchange only. Publishing an `OrderPlaced` sends to `OrderPlaced`'s exchange, which the audit queue is not bound to, so the message never arrives.

The fix is explicit: register a `HandlerReference` for each derived type the subscriber should receive.

```csharp
var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(DomainEvent) },
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(OrderPlaced) },
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(OrderShipped) },
};
services.AddSingleton<IList<HandlerReference>>(handlerReferences);

services.AddServiceConnect(b =>
{
    b.ConfigureBus(cfg => cfg.ScanForMessageHandlers = false);
    // …
});
```

This is a deliberate teaching moment for the sample. It is the real friction of the pattern and it belongs on the page.

## Sample: layout

```
examples/PolymorphicMessages/
├── src/
│   ├── ServiceConnect.Examples.PolymorphicMessages.Contracts/
│   │   ├── DomainEvent.cs
│   │   ├── OrderPlaced.cs
│   │   └── OrderShipped.cs
│   ├── ServiceConnect.Examples.PolymorphicMessages.Publisher/
│   │   └── Program.cs
│   ├── ServiceConnect.Examples.PolymorphicMessages.AuditSubscriber/
│   │   ├── DomainEventHandler.cs
│   │   └── Program.cs
│   └── ServiceConnect.Examples.PolymorphicMessages.ShippingSubscriber/
│       ├── OrderShippedHandler.cs
│       └── Program.cs
├── PolymorphicMessages.sln
├── README.md
├── run.sh
└── run.ps1
```

The layout matches the eleven existing samples exactly. Contracts project is shared by publisher and both subscribers.

## Sample: behaviour

The publisher sends one `OrderPlaced` followed by one `OrderShipped`, both with the same correlation id. The audit subscriber handles `DomainEvent` and prints an audit line for each; the shipping subscriber handles `OrderShipped` and prints a shipping line for that one event.

Expected output (with the caveat that lines from concurrent subscribers may interleave — matching how other samples phrase this):

```
READY:audit-subscriber
READY:shipping-subscriber
SUCCESS:polymorphic-messages-publisher:published order-placed order-42
SUCCESS:audit-subscriber:audited OrderPlaced order-42
SUCCESS:polymorphic-messages-publisher:published order-shipped order-42
SUCCESS:audit-subscriber:audited OrderShipped order-42
SUCCESS:shipping-subscriber:processed order-shipped order-42
```

The value on the page is the two `audit-subscriber` lines: one handler, two different concrete types. That is the pattern's payoff in one visible line.

### Audit subscriber wiring

The audit subscriber **disables scanning** and registers explicit `HandlerReference`s for `DomainEvent`, `OrderPlaced`, and `OrderShipped`. It registers `DomainEventHandler` in DI as `IMessageHandler<DomainEvent>`. This is the non-obvious wiring the pattern page will cite.

### Shipping subscriber wiring

The shipping subscriber uses default scanning. Its `OrderShippedHandler` implements `IMessageHandler<OrderShipped>` and the scanner registers it normally. No manual handler references.

### Publisher wiring

Standard publisher: no handlers, just `IBus.PublishAsync` for each event. Identical in shape to the PublishSubscribe publisher.

### README and run scripts

README follows the exact template of [`examples/PublishSubscribe/README.md`](../../../examples/PublishSubscribe/README.md): overview, participants, mermaid sequence diagram, prerequisites, run command, manual invocation, expected output, "what to notice" section. The "what to notice" paragraph calls out the base-type dispatch and the explicit `HandlerReference` registration.

`run.sh` and `run.ps1` start both subscribers, wait for `READY:` lines, then start the publisher. Same shape as the other samples' run scripts.

## Website: pattern page

New file: `website/src/content/docs/learn/messaging-patterns/polymorphic-messages.mdx`.

Sections (target length ~250 lines, matching `pub-sub.mdx`):

1. **What it is.** One-paragraph definition: publish derived, handle on base, one subscriber catches many concrete types through a shared contract.
2. **When to use.** Cross-cutting subscribers — audit, metrics, outbox, archival — that care about a category of events rather than specific ones. Also: internal refactoring where a new concrete event should automatically flow to existing base-type handlers.
3. **The contract hierarchy.** Code block defining `DomainEvent`, `OrderPlaced`, `OrderShipped`. Keep it single-level. Note the `abstract` modifier on the base and `sealed` on the leaves.
4. **The handlers.** Code blocks for `DomainEventHandler : IMessageHandler<DomainEvent>` and `OrderShippedHandler : IMessageHandler<OrderShipped>`.
5. **Wiring a base-type subscriber.** The `HandlerReference` block. Explain *why* it's needed — subscription setup does not walk the hierarchy; dispatch does. Frame it as deliberate rather than accidental.
6. **Dispatch vs. subscription.** Two-sentence explainer reinforcing point 5.
7. **Trade-offs.** Polymorphism gives clean categorisation but couples derived types' serialised shape to the base. Stay single-level. For polyglot consumers, prefer composition (shared header fields) over inheritance.
8. **Reference and what comes next.** Links to the sample, the `messages.mdx` page, and the Pub/Sub page.

## Website: `messages.mdx` update

Replace the paragraph at [`messages.mdx:80-82`](../../../website/src/content/docs/learn/core-concepts/messages.mdx#L80-L82) ("Avoid cross-service inheritance …") with:

> **Inheritance is supported, but use it deliberately.** A single level of inheritance (`OrderPlaced : DomainEvent : Message`) lets one handler catch a whole category of events — useful for audit, metrics, and outbox subscribers. See [Polymorphic Messages](/ServiceConnect-CSharp/learn/messaging-patterns/polymorphic-messages/) for the pattern. Keep the hierarchy shallow: deep trees make the serialised shape harder to reason about, especially for polyglot consumers.

No other changes to `messages.mdx`.

## Website: samples catalog update

Add a new section to [`samples.mdx`](../../../website/src/content/docs/samples.mdx), placed after "Content-Based Routing" to match the sidebar ordering:

```markdown
### Polymorphic Messages

Publish derived events; a base-type handler catches the whole category while specific handlers catch one type.

- Pattern: [Polymorphic Messages](/ServiceConnect-CSharp/learn/messaging-patterns/polymorphic-messages/)
- Source: [`examples/PolymorphicMessages`](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/examples/PolymorphicMessages)
```

## Website: sidebar navigation

In [`astro.config.mjs`](../../../website/astro.config.mjs), add one entry under "Messaging Patterns", positioned after "Content-Based Routing":

```javascript
{ label: 'Polymorphic Messages', link: '/learn/messaging-patterns/polymorphic-messages/' },
```

Rationale for position: Content-Based Routing and Polymorphic Messages are conceptually adjacent — both answer "which subscriber sees which message" — and placing them together makes the sidebar read naturally.

## Testing and verification

- **Library behaviour.** Already covered by [`PolymorphicMessageTests.cs`](../../../src/ServiceConnect.EndToEndTests/Routing/PolymorphicMessageTests.cs). No new tests needed.
- **Sample build.** `dotnet build examples/PolymorphicMessages/PolymorphicMessages.sln` must produce zero warnings and zero errors.
- **Sample run.** `bash examples/PolymorphicMessages/run.sh` against a running `docker compose -f examples/docker-compose.yml up -d` must produce the expected output above, with every `READY:` and `SUCCESS:` line present.
- **Website build.** `npm run build` in `website/` must succeed with no broken links — Astro's link checker will catch the new internal links.
- **Website preview.** `npm run dev` in `website/`, navigate to the new page and the modified `messages.mdx`, confirm sidebar shows the new entry in the expected slot, confirm the samples catalog shows the new section, confirm all added links resolve.

## Risks and mitigations

- **Risk:** sample works locally but not in CI (Docker timing). **Mitigation:** model the run script on `examples/PublishSubscribe/run.sh` — it is the closest shape and is known to work in CI.
- **Risk:** the softened `messages.mdx` guidance undersells the polyglot-consumer caveat. **Mitigation:** keep the "keep the hierarchy shallow" sentence explicit, and the trade-offs section on the new page goes further.
- **Risk:** readers miss the `HandlerReference` gotcha and hit a silent "my handler isn't firing" failure. **Mitigation:** the pattern page puts the gotcha *above* the "it just works" framing — the section is titled "Wiring a base-type subscriber" and is non-skippable when reading top-to-bottom.

## Open questions

None at time of writing. The design has been walked through with the user and each section approved before moving to the next.
