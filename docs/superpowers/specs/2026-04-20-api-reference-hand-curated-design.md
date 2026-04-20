# Hand-Curated API Reference — Design

## Goal

Replace the DocFX-generated API reference at `/api/` with hand-curated MDX pages in the existing Starlight site. Every type a consumer calls, implements, or configures against gets a page. Organised into two tiers:

- **API** — the primary consumer surface (types devs call, implement, or decorate messages with).
- **Extension Points** — pluggable internals only touched when replacing a default (custom persistence, serializer, transport).

Modelled on react.dev's reference: prose-first, formal signature in a code block, worked examples, consistent per-page template, pitfalls in callouts.

## Why not DocFX

DocFX auto-generates one page per public type in every assembly. That includes a lot of internal plumbing (`Retry`, `Connection`, `HandlerReference`, `HeaderDecoder`, `RabbitMqTopologyProvisioner`, …) that a consumer never touches. Filtering the output type-by-type is a constant maintenance tax; the XML doc comments also don't carry the narrative voice we want.

The public API rarely changes, so the authoring burden of hand-curated pages is bounded and amortises well. XML doc comments stay in source for IDE IntelliSense — they just don't feed a website any more.

## Tier assignment principle

A type lands in **API** if a typical consumer directly calls it, implements it, or decorates messages with it.

A type lands in **Extension Points** if it's only touched when replacing a default implementation.

When in doubt, prefer API — the cost of someone discovering an unfamiliar type in the primary reference is lower than the cost of missing a type that turned out to be consumer-facing.

## Information architecture

### URL structure

Hierarchical under `/reference/`, grouped by concept:

```
/reference/                              → landing (overview + grouped index)
/reference/bus/ibus/
/reference/bus/ibusconfiguration/
/reference/bus/add-serviceconnect/
/reference/messages/message/
/reference/messages/envelope/
/reference/messages/attributes/
/reference/handlers/imessagehandler/
/reference/handlers/istreamhandler/
/reference/handlers/iconsumecontext/
/reference/handlers/event-args/
/reference/configuration/itransportconfiguration/
/reference/configuration/iqueueconfiguration/
/reference/configuration/ipersistenceconfiguration/
/reference/configuration/ipipelineconfiguration/
/reference/process-managers/iprocesshandler/
/reference/process-managers/iprocessmanagerdata/
/reference/process-managers/iprocessmanagerpropertymapper/
/reference/process-managers/aggregator/
/reference/filters/ifilter/
/reference/filters/imessageprocessingmiddleware/
/reference/filters/isendmessagemiddleware/

/reference/extension-points/                                 → landing
/reference/extension-points/persistence/iaggregatorpersistor/
/reference/extension-points/persistence/iprocessmanagerfinder/
/reference/extension-points/persistence/ileaseawaretimeoutstore/
/reference/extension-points/serialization/imessageserializer/
/reference/extension-points/serialization/imessagetyperegistry/
/reference/extension-points/transport/iserviceconnectconnection/
/reference/extension-points/transport/iconsumer/
/reference/extension-points/transport/iproducer/
/reference/extension-points/registry/ihandlerregistry/
/reference/extension-points/registry/imessagedispatcher/
/reference/extension-points/registry/imessageprocessor/
```

### Page inventory

The tables below are the authoritative set of pages this spec creates. Each row is one MDX file under `website/src/content/docs/` matching its URL. If the implementation plan discovers a consumer-facing type not listed here, it should flag it for a spec update before adding a page.

**Landing pages**
- `reference/index.mdx` — top-level API reference overview with grouped links and short descriptions.
- `reference/extension-points/index.mdx` — short landing explaining "you only need this section if you're replacing a default implementation" + grouped links.

**API — primary surface**

| Group | Type | Page |
|-------|------|------|
| Bus | `IBus` | `reference/bus/ibus.mdx` |
| Bus | `IBusConfiguration` | `reference/bus/ibusconfiguration.mdx` |
| Bus | `AddServiceConnect` | `reference/bus/add-serviceconnect.mdx` |
| Messages | `Message` | `reference/messages/message.mdx` |
| Messages | `Envelope` | `reference/messages/envelope.mdx` |
| Messages | Attributes (`Route`, `MessageEndpoint`, others) | `reference/messages/attributes.mdx` |
| Handlers | `IMessageHandler<T>` | `reference/handlers/imessagehandler.mdx` |
| Handlers | `IStreamHandler` | `reference/handlers/istreamhandler.mdx` |
| Handlers | `IConsumeContext` | `reference/handlers/iconsumecontext.mdx` |
| Handlers | `ConsumeEventArgs`, `ConsumeEventResult`, `PublishEventArgs`, `SendEventArgs`, `OutgoingEventArgs` | `reference/handlers/event-args.mdx` |
| Configuration | `ITransportConfiguration` | `reference/configuration/itransportconfiguration.mdx` |
| Configuration | `IQueueConfiguration` | `reference/configuration/iqueueconfiguration.mdx` |
| Configuration | `IPersistenceConfiguration` | `reference/configuration/ipersistenceconfiguration.mdx` |
| Configuration | `IPipelineConfiguration` | `reference/configuration/ipipelineconfiguration.mdx` |
| Process Managers | `IProcessHandler` | `reference/process-managers/iprocesshandler.mdx` |
| Process Managers | `IProcessManagerData` | `reference/process-managers/iprocessmanagerdata.mdx` |
| Process Managers | `IProcessManagerPropertyMapper` | `reference/process-managers/iprocessmanagerpropertymapper.mdx` |
| Process Managers | `Aggregator<T>`, `AggregatorSnapshot` | `reference/process-managers/aggregator.mdx` |
| Filters & Middleware | `IFilter`, `IFilterPipeline` | `reference/filters/ifilter.mdx` |
| Filters & Middleware | `IMessageProcessingMiddleware` | `reference/filters/imessageprocessingmiddleware.mdx` |
| Filters & Middleware | `ISendMessageMiddleware`, `ISendMessagePipeline` | `reference/filters/isendmessagemiddleware.mdx` |

**Extension Points**

| Group | Type | Page |
|-------|------|------|
| Persistence | `IAggregatorPersistor` | `reference/extension-points/persistence/iaggregatorpersistor.mdx` |
| Persistence | `IProcessManagerFinder` | `reference/extension-points/persistence/iprocessmanagerfinder.mdx` |
| Persistence | `ILeaseAwareTimeoutStore` | `reference/extension-points/persistence/ileaseawaretimeoutstore.mdx` |
| Serialization | `IMessageSerializer` | `reference/extension-points/serialization/imessageserializer.mdx` |
| Serialization | `IMessageTypeRegistry` | `reference/extension-points/serialization/imessagetyperegistry.mdx` |
| Transport | `IServiceConnectConnection` | `reference/extension-points/transport/iserviceconnectconnection.mdx` |
| Transport | `IConsumer` | `reference/extension-points/transport/iconsumer.mdx` |
| Transport | `IProducer` | `reference/extension-points/transport/iproducer.mdx` |
| Registry | `IHandlerRegistry` | `reference/extension-points/registry/ihandlerregistry.mdx` |
| Registry | `IMessageDispatcher` | `reference/extension-points/registry/imessagedispatcher.mdx` |
| Registry | `IMessageProcessor` | `reference/extension-points/registry/imessageprocessor.mdx` |

### Starlight sidebar

Replace the existing `API Reference` link (currently pointing at `/api/index.html`) with two top-level sidebar entries in `website/astro.config.mjs`. Each group in the inventory tables above becomes a nested sidebar group; each inventory row becomes a `{ label, link }` item. For example, the Bus group renders as:

```js
{
  label: 'Bus',
  items: [
    { label: 'IBus', link: '/reference/bus/ibus/' },
    { label: 'IBusConfiguration', link: '/reference/bus/ibusconfiguration/' },
    { label: 'AddServiceConnect', link: '/reference/bus/add-serviceconnect/' },
  ],
},
```

The top-level shape is:

```
API Reference
├── Overview (link: /reference/)
├── Bus
├── Messages
├── Handlers
├── Configuration
├── Process Managers
└── Filters & Middleware

Extension Points
├── Overview (link: /reference/extension-points/)
├── Persistence
├── Serialization
├── Transport
└── Registry
```

Starlight's base-prefix automatically prefixes sidebar `link:` values, so these paths stay relative to the base.

## Page template

Every reference page uses the same MDX structure:

````mdx
---
title: IBus
description: The runtime API for publishing, sending, and request/reply.
---

import { Aside } from '@astrojs/starlight/components';

## Overview

One or two sentences: what this is, when a dev reaches for it. Link into
the relevant Learn page if the concept needs deeper explanation.

## Reference

### `PublishAsync<T>`

```csharp
Task PublishAsync<T>(T message, Dictionary<string, string>? headers = null,
                    CancellationToken cancellationToken = default)
    where T : class;
```

Publishes a message to all subscribers on the configured exchange.

**Parameters**
- `message` — the message instance. Must be a reference type.
- `headers` — optional string/string headers propagated with the message.
- `cancellationToken` — cancels the publish before the broker acknowledges.

**Returns.** A task that completes when the broker has acknowledged the publish.

**Remarks.** Routing is determined by the message type, not the parameter.
Subscribers are matched by `[Route]` on the message class.

<Aside type="caution" title="Ordering">
  Publishes across different message types are not ordered relative to each other.
</Aside>

---

### `SendAsync<T>`

(next member…)

## Usage

### Publishing a domain event

```csharp
public class OrderService(IBus bus)
{
    public Task PlaceOrder(Order order, CancellationToken ct) =>
        bus.PublishAsync(new OrderPlaced(order.Id, order.Total), ct);
}
```

Prose explaining when/why, any gotchas.

### Request/reply across a boundary

(worked example with prose)

## See also

- [The Bus](/ServiceConnect-CSharp/learn/core-concepts/the-bus/) — concept
- [`IBusConfiguration`](../ibusconfiguration/) — how this gets wired up
- [Pub/Sub](/ServiceConnect-CSharp/learn/messaging-patterns/pub-sub/) — pattern
````

**Section order.** Overview → Reference → Usage → See also. Every page has all four sections. Pages that represent a group of small related types (e.g., `attributes.mdx`, `event-args.mdx`) get one `### <TypeName>` sub-heading per type inside **Reference**, then one unified **Usage** section showing them in context.

**Extension-point pages** include one extra section — **Implementing** — between Reference and Usage. It explains the contract a custom implementation must satisfy: invariants, threading model, what the framework guarantees to pass, what it expects back, error-handling contract.

**Signature rendering.** Plain C# fenced code blocks with Starlight's default Shiki highlighting. One block per member; the member heading becomes the anchor (`#publishasync`, `#sendasync`). No custom signature component — keeps the maintenance surface flat.

**Callouts.** Use Starlight's `<Aside type="note|tip|caution|danger">` for caveats, pitfalls, and "required when…" notes. This is the pitfall-box pattern react.dev uses.

**Frontmatter.**
- `title` — the type name exactly as written in C# (generics preserved: `IMessageHandler<T>`).
- `description` — one-sentence summary; shows in search results and link cards.

No `tableOfContents` overrides — Starlight's defaults render h2/h3 in the right-hand outline, which gives the member-level index we want for free.

## Landing pages

**`reference/index.mdx`** — short intro paragraph, then Starlight `<CardGrid>` of `<LinkCard>` entries — one card per group (Bus, Messages, Handlers, …) linking to the first page in that group, with a one-line description. Mirrors the structure of the existing homepage `FeatureGrid`.

**`reference/extension-points/index.mdx`** — opens with a short "you only need this section if you're replacing a default implementation" framing, then `<CardGrid>` of the four groups (Persistence, Serialization, Transport, Registry).

## DocFX removal

Single cleanup task covering:

- Delete `website/docfx/` (config, toc, filter, index.md, templates, assets).
- Delete `website/_docfx_metadata/` (generated).
- Delete `website/public/api/` (generated output; currently gitignored but purge locally).
- Remove any DocFX-related gitignore entries that become dead (keep entries that guard against accidental regeneration).
- Update `website/astro.config.mjs`: remove the `API Reference` sidebar entry at `/api/index.html`; add the two new top-level entries (see sidebar section above).
- Update `website/src/content/docs/index.mdx` hero actions: point the "API Reference" button at `/reference/` instead of `/api/index.html`.
- Remove any CI step that invokes `dotnet docfx` (check GitHub Actions workflows before deletion).

XML doc comments in `src/` stay untouched — they continue to power IDE IntelliSense. They are no longer the source of truth for website content.

## Learn cross-linking

Every reference page links back to at least one Learn page via **See also**. Every Learn page that discusses one of the reference types gets a short **Reference** section at the bottom linking to the corresponding pages. No rewrites of existing Learn prose — purely additive.

Representative mapping:

| Learn page | Reference links |
|------------|-----------------|
| `learn/core-concepts/the-bus/` | `reference/bus/ibus/`, `reference/bus/ibusconfiguration/`, `reference/bus/add-serviceconnect/` |
| `learn/core-concepts/messages/` | `reference/messages/message/`, `reference/messages/envelope/`, `reference/messages/attributes/` |
| `learn/core-concepts/handlers/` | `reference/handlers/imessagehandler/`, `reference/handlers/iconsumecontext/` |
| `learn/core-concepts/endpoints/` | `reference/configuration/itransportconfiguration/`, `reference/configuration/iqueueconfiguration/` |
| `learn/messaging-patterns/pub-sub/` | `reference/bus/ibus/#publishasync`, `reference/messages/attributes/` |
| `learn/messaging-patterns/point-to-point/` | `reference/bus/ibus/#sendasync` |
| `learn/messaging-patterns/request-reply/` | `reference/bus/ibus/#requestasync` |
| `learn/messaging-patterns/process-manager/` | the whole `reference/process-managers/` group |
| `learn/messaging-patterns/aggregator/` | `reference/process-managers/aggregator/` |
| `learn/messaging-patterns/filters/` | `reference/filters/ifilter/`, `reference/filters/imessageprocessingmiddleware/`, `reference/filters/isendmessagemiddleware/` |
| `learn/messaging-patterns/streaming/` | `reference/handlers/istreamhandler/` |
| `learn/operations/configuration/` | all of `reference/configuration/` |

## Voice & style

Prose-first. Every member has at least one sentence of plain-English description above the parameters list. Examples are realistic (not `Foo`/`Bar`) and match the vocabulary in the Learn section — `OrderPlaced`, `OrderService`, `ShippingSaga`.

Avoid restating what the code says. Describe *why* a dev would reach for something, *when* it's the right tool, and *what surprises* to watch for.

`<Aside type="caution">` for gotchas. `<Aside type="tip">` for non-obvious usage patterns. `<Aside type="note">` sparingly — most "notes" belong in prose.

## Out of scope

- Auto-generation of reference pages from XML doc comments.
- Versioning of reference pages across library releases (deferred; when the API does change, pages are edited in place and PRs are reviewed against the diff).
- Search tuning beyond Starlight's defaults.
- Breaking changes to the existing Learn section.
- Additional conceptual documentation — the Learn section already covers that.

## Success criteria

- Every type listed in the page inventory has a published reference page.
- Sidebar shows `API Reference` and `Extension Points` groups with the structure specified above; the old `/api/index.html` link is gone.
- Homepage hero "API Reference" action links to `/reference/`.
- DocFX-related files, config, and CI steps are removed from the repo.
- Each Learn page has a **Reference** section linking into the new pages.
- `astro build` completes cleanly with no broken internal links reported by Starlight.
