# Hand-Curated API Reference Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the DocFX-generated API reference at `/api/` with ~30 hand-authored Starlight MDX pages, tiered into an "API Reference" section (primary consumer surface) and an "Extension Points" section (pluggable internals).

**Architecture:** Every reference page is an MDX file under `website/src/content/docs/reference/` following a shared template (Overview / Reference / Usage / See also). Extension-point pages add an "Implementing" section. Pages are grouped in the Starlight sidebar by concept (Bus, Messages, Handlers, Configuration, Process Managers, Filters). DocFX and its generated output are removed. Cross-links are added between Learn and Reference pages.

**Tech Stack:** Astro 6, Starlight 0.38, MDX. Starlight's `<Aside>`, `<CardGrid>`, `<LinkCard>` components. C# fenced code blocks with Shiki highlighting. npm-based build (`npm run build` in `website/`).

**Spec:** [docs/superpowers/specs/2026-04-20-api-reference-hand-curated-design.md](../specs/2026-04-20-api-reference-hand-curated-design.md)

---

## Conventions

### Reference page template (primary API)

Every primary API reference page starts from this template. Fill in the angle-bracketed placeholders. Do not add or remove top-level sections.

````mdx
---
title: <TypeName>
description: <one-sentence summary>
---

import { Aside } from '@astrojs/starlight/components';

## Overview

<1-2 sentences: what the type is, when a dev reaches for it. Link the
relevant Learn page if the concept needs deeper explanation.>

## Reference

### `<MemberName>`

```csharp
<formal C# signature — single fenced block per member>
```

<1-sentence description of what this member does.>

**Parameters**
- `<name>` — <description>

**Returns.** <what the return value represents — omit section for void/property getters>

**Remarks.** <caveats, invariants, or non-obvious behaviour — omit if none>

<Aside type="caution" title="<short heading>">
  <pitfall prose — only when there is a real, known gotcha>
</Aside>

---

### `<NextMember>`

<repeat per member. For small group pages that cover multiple types,
use an h2 per type and an h3 per member within each.>

## Usage

### <Scenario name>

```csharp
<realistic worked example — use OrderService / OrderPlaced / ShippingSaga
vocabulary, never Foo/Bar>
```

<prose explaining when/why the scenario applies, and any gotchas.>

## See also

- [<Learn page title>](/ServiceConnect-CSharp/learn/<path>/) — concept
- [`<Related type>`](../<related-type>/) — related reference page
````

### Reference page template (extension points)

Extension-point pages use the same template with **one extra section** inserted between Reference and Usage:

````mdx
## Implementing

<Explain the contract a custom implementation must satisfy: invariants,
threading model, what the framework guarantees to pass, what it expects
back, error-handling contract.>

```csharp
<skeletal implementation showing the shape — class/method stubs, no
business logic>
```
````

### Authoring rules

- **Overview is prose-first, not a restatement of the signature.** Tell the reader *when* they'd reach for this type.
- **Every member has at least one sentence of plain-English description** above its parameter list.
- **Realistic vocabulary only.** Use `OrderService`, `OrderPlaced`, `ShippingSaga`, `PaymentProcessor`. Never `Foo`, `Bar`, `DoSomething`.
- **`<Aside type="caution">`** for gotchas. **`<Aside type="tip">`** for non-obvious usage. **`<Aside type="note">`** sparingly — most "notes" belong in prose.
- **Member headings** must match the C# identifier exactly, wrapped in backticks (`### \`PublishAsync\``). Generics in the heading are literal: `### \`PublishAsync<T>\``.
- **Signatures** must match the source exactly, including optional parameters with default values and `where T : Message` constraints.
- **See also must cite at least one Learn page** plus at least one related reference page. Extension-point pages may cite the primary-API type they extend.

### Verification commands

The implementer runs these after every content change:

```bash
cd website
npm run build
```

Expected: build completes; no `Invalid link` or `Page not found` warnings in output. Any such warning is a failure; fix before moving on.

Optional visual QA during authoring:

```bash
cd website
npm run dev
```

Then open the page at `http://localhost:4321/ServiceConnect-CSharp/reference/<path>/`.

---

## File Structure

### Created

```
website/src/content/docs/reference/
├── index.mdx                                 (API landing)
├── bus/
│   ├── ibus.mdx
│   ├── ibusconfiguration.mdx
│   └── add-serviceconnect.mdx
├── messages/
│   ├── message.mdx
│   ├── envelope.mdx
│   └── options.mdx
├── handlers/
│   ├── imessagehandler.mdx
│   ├── istreamhandler.mdx
│   ├── iconsumecontext.mdx
│   └── event-args.mdx
├── configuration/
│   ├── itransportconfiguration.mdx
│   ├── iqueueconfiguration.mdx
│   ├── ipersistenceconfiguration.mdx
│   └── ipipelineconfiguration.mdx
├── process-managers/
│   ├── iprocesshandler.mdx
│   ├── iprocessmanagerdata.mdx
│   ├── iprocessmanagerpropertymapper.mdx
│   └── aggregator.mdx
├── filters/
│   ├── ifilter.mdx
│   ├── imessageprocessingmiddleware.mdx
│   └── isendmessagemiddleware.mdx
└── extension-points/
    ├── index.mdx                             (Extension Points landing)
    ├── persistence/
    │   ├── iaggregatorpersistor.mdx
    │   ├── iprocessmanagerfinder.mdx
    │   └── ileaseawaretimeoutstore.mdx
    ├── serialization/
    │   ├── imessageserializer.mdx
    │   └── imessagetyperegistry.mdx
    ├── transport/
    │   ├── iserviceconnectconnection.mdx
    │   ├── iconsumer.mdx
    │   └── iproducer.mdx
    └── registry/
        ├── ihandlerregistry.mdx
        ├── imessagedispatcher.mdx
        └── imessageprocessor.mdx
```

### Modified

- `website/astro.config.mjs` — sidebar restructure
- `website/src/content/docs/index.mdx` — hero action URL
- `website/src/content/docs/learn/core-concepts/the-bus.mdx`
- `website/src/content/docs/learn/core-concepts/messages.mdx`
- `website/src/content/docs/learn/core-concepts/handlers.mdx`
- `website/src/content/docs/learn/core-concepts/endpoints.mdx`
- `website/src/content/docs/learn/messaging-patterns/pub-sub.mdx`
- `website/src/content/docs/learn/messaging-patterns/point-to-point.mdx`
- `website/src/content/docs/learn/messaging-patterns/request-reply.mdx`
- `website/src/content/docs/learn/messaging-patterns/process-manager.mdx`
- `website/src/content/docs/learn/messaging-patterns/aggregator.mdx`
- `website/src/content/docs/learn/messaging-patterns/filters.mdx`
- `website/src/content/docs/learn/messaging-patterns/streaming.mdx`
- `website/src/content/docs/learn/operations/configuration.mdx`
- `.github/workflows/docs.yml` — remove DocFX steps

### Deleted

- `website/docfx/` (whole directory)
- `website/_docfx_metadata/` (generated)
- `website/public/api/` (generated)
- `.config/dotnet-tools.json` (DocFX is the only tool)

---

## Task Overview

1. **Bootstrap** — landing page stubs, hero action, sidebar skeleton
2. **Remove DocFX** — delete files, update CI workflow, remove dotnet tools
3. **Bus group** — IBus, IBusConfiguration, AddServiceConnect
4. **Messages group** — Message, Envelope, options (PublishOptions/SendOptions/RequestOptions)
5. **Handlers group** — IMessageHandler, IStreamHandler, IConsumeContext, event-args
6. **Configuration group** — ITransportConfiguration, IQueueConfiguration, IPersistenceConfiguration, IPipelineConfiguration
7. **Process Managers group** — IProcessHandler, IProcessManagerData, IProcessManagerPropertyMapper, Aggregator
8. **Filters & Middleware group** — IFilter+IFilterPipeline, IMessageProcessingMiddleware, ISendMessageMiddleware+ISendMessagePipeline
9. **Extension Points: Persistence** — IAggregatorPersistor, IProcessManagerFinder, ILeaseAwareTimeoutStore
10. **Extension Points: Serialization** — IMessageSerializer, IMessageTypeRegistry
11. **Extension Points: Transport** — IServiceConnectConnection, IConsumer, IProducer
12. **Extension Points: Registry** — IHandlerRegistry, IMessageDispatcher, IMessageProcessor
13. **Expand landing pages** — add CardGrid link cards to both landings
14. **Learn cross-links** — add `## Reference` sections to Learn pages
15. **Final verification** — full site build + visual spot-check of 5 pages

---

## Task 1: Bootstrap — landings, hero, sidebar skeleton

**Files:**
- Create: `website/src/content/docs/reference/index.mdx`
- Create: `website/src/content/docs/reference/extension-points/index.mdx`
- Modify: `website/src/content/docs/index.mdx` (hero action URL)
- Modify: `website/astro.config.mjs` (sidebar)

- [ ] **Step 1: Create the API landing page stub**

Create `website/src/content/docs/reference/index.mdx` with:

```mdx
---
title: API Reference
description: Reference documentation for the ServiceConnect public API — every type a consumer calls, implements, or configures.
---

Reference documentation for the ServiceConnect public API. Every type a consumer calls, implements, or configures has a page here.

Organised into two tiers:

- **API Reference** — the primary consumer surface: the bus, handlers, messages, configuration, process managers, filters.
- **Extension Points** — pluggable internals you only touch when replacing a default (custom persistence, serializer, or transport).

Use the sidebar to browse. Link cards covering each group will land here in a follow-up.

> Want to learn how to use ServiceConnect, not just look up a method? Start with [Getting Started](/ServiceConnect-CSharp/learn/getting-started/).
```

- [ ] **Step 2: Create the Extension Points landing page stub**

Create `website/src/content/docs/reference/extension-points/index.mdx` with:

```mdx
---
title: Extension Points
description: Pluggable internals — only touch these when replacing a default implementation.
---

Extension points are the interfaces the framework calls *into*. You only need this section if you're replacing a default implementation — shipping a custom persistence store, serializer, or transport.

Most consumers never open this section. If you're wiring the bus into a new database or message broker, start here; otherwise the [API Reference](/ServiceConnect-CSharp/reference/) has what you need.

Link cards covering each extension-point group will land here in a follow-up.
```

- [ ] **Step 3: Update the homepage hero action**

Edit `website/src/content/docs/index.mdx`. Find the hero action for the API reference and change its link from `/ServiceConnect-CSharp/api/index.html` to `/ServiceConnect-CSharp/reference/`. The actions block should read:

```yaml
  actions:
    - text: Get Started
      link: /ServiceConnect-CSharp/learn/getting-started/
      icon: right-arrow
      variant: primary
    - text: API Reference
      link: /ServiceConnect-CSharp/reference/
      icon: open-book
      variant: secondary
    - text: View on GitHub
      link: https://github.com/R-Suite/ServiceConnect-CSharp
      icon: external
      variant: secondary
```

- [ ] **Step 4: Replace the sidebar entry in astro.config.mjs**

Edit `website/astro.config.mjs`. The current sidebar has an entry:

```js
{
  label: 'API Reference',
  link: '/api/index.html',
},
```

Replace it with two top-level groups. The nested groups (Bus, Messages, etc.) start empty — later tasks populate them:

```js
{
  label: 'API Reference',
  items: [
    { label: 'Overview', link: '/reference/' },
    { label: 'Bus', items: [] },
    { label: 'Messages', items: [] },
    { label: 'Handlers', items: [] },
    { label: 'Configuration', items: [] },
    { label: 'Process Managers', items: [] },
    { label: 'Filters & Middleware', items: [] },
  ],
},
{
  label: 'Extension Points',
  items: [
    { label: 'Overview', link: '/reference/extension-points/' },
    { label: 'Persistence', items: [] },
    { label: 'Serialization', items: [] },
    { label: 'Transport', items: [] },
    { label: 'Registry', items: [] },
  ],
},
```

Place these two groups after the existing `Learn` group and before `Samples`. Keep the `Samples` and `Releases` entries unchanged.

- [ ] **Step 5: Verify build**

Run:

```bash
cd website && npm run build
```

Expected: build completes; the two new top-level sidebar entries appear; no `Invalid link` warnings. Starlight may warn about empty groups — that's fine, they're populated in later tasks. If you see genuine link failures, stop and fix.

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/index.mdx \
        website/src/content/docs/reference/extension-points/index.mdx \
        website/src/content/docs/index.mdx \
        website/astro.config.mjs
git commit -m "docs: bootstrap hand-curated API reference landings and sidebar"
```

---

## Task 2: Remove DocFX

**Files:**
- Delete: `website/docfx/` (directory)
- Delete: `website/_docfx_metadata/` (directory)
- Delete: `website/public/api/` (directory)
- Delete: `.config/dotnet-tools.json`
- Modify: `.github/workflows/docs.yml`

- [ ] **Step 1: Delete the DocFX source directory**

```bash
rm -rf website/docfx
```

This removes `docfx.json`, `toc.yml`, `index.md`, `filterConfig.yml`, the custom `templates/` folder, and the `assets/` logo copies.

- [ ] **Step 2: Delete the DocFX generated metadata**

```bash
rm -rf website/_docfx_metadata
```

- [ ] **Step 3: Delete the DocFX build output**

```bash
rm -rf website/public/api
```

- [ ] **Step 4: Delete the dotnet tools manifest**

```bash
rm .config/dotnet-tools.json
```

Then remove the now-empty `.config` directory only if it contains no other files:

```bash
rmdir .config 2>/dev/null || true
```

- [ ] **Step 5: Update the GitHub Actions workflow**

Edit `.github/workflows/docs.yml`. Rewrite it to remove all DocFX-related steps and .NET setup. The full post-edit contents should be:

```yaml
name: Build and deploy docs site

on:
  push:
    branches: [master]
    paths:
      - 'website/**'
      - '.github/workflows/docs.yml'
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: website/package-lock.json

      - name: Install website dependencies
        working-directory: website
        run: npm ci

      - name: Build website (Astro + Starlight)
        working-directory: website
        run: npm run build

      - name: Setup Pages
        uses: actions/configure-pages@v5

      - name: Upload Pages artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: website/dist

  deploy:
    needs: build
    if: github.ref == 'refs/heads/master'
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

Changes from the previous version: dropped the `src/**`, `filters/**`, `**/*.csproj`, `**/*.cs`, and `.config/dotnet-tools.json` path triggers; dropped the `Setup .NET`, `Restore .NET tools (DocFX)`, `Build .NET solution`, and `Build API reference (DocFX)` steps.

- [ ] **Step 6: Verify build**

```bash
cd website && npm run build
```

Expected: build completes; no references to `/api/` in the generated output (since the directory was deleted). Confirm homepage hero action now points at `/reference/`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "docs: remove DocFX and its generated output

Hand-curated reference under /reference/ replaces DocFX-generated pages.
XML doc comments stay in source for IDE IntelliSense."
```

---

## Task 3: Bus group — IBus, IBusConfiguration, AddServiceConnect

**Files:**
- Read: `src/ServiceConnect.Interfaces/Bus/IBus.cs`, `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`, `src/ServiceConnect/ServiceCollectionExtensions.cs` (grep for `AddServiceConnect` if path differs)
- Create: `website/src/content/docs/reference/bus/ibus.mdx`
- Create: `website/src/content/docs/reference/bus/ibusconfiguration.mdx`
- Create: `website/src/content/docs/reference/bus/add-serviceconnect.mdx`
- Modify: `website/astro.config.mjs` (populate `Bus` sidebar group)

Each page follows the **primary API template** from Conventions. Fill in the template using the source file for signatures and member lists, and the Learn pages for cross-links.

- [ ] **Step 1: Author `ibus.mdx`**

Read `src/ServiceConnect.Interfaces/Bus/IBus.cs`. Create `website/src/content/docs/reference/bus/ibus.mdx` using the primary API template. Specifics for this page:

- **Overview:** describe `IBus` as the runtime surface for moving messages — publish, send, request/reply, routing slip, timeouts, streaming, consumer lifecycle. Link to `/ServiceConnect-CSharp/learn/core-concepts/the-bus/`.
- **Reference — one h3 per member.** Cover every public member on `IBus`: `PublishAsync<T>`, `SendAsync<T>`, `SendRequestAsync<T, TReply>`, `SendRequestMultiAsync<T, TReply>`, `PublishRequestAsync<TRequest, TReply>`, `RouteAsync<T>`, `CreateStream<T>`, `StartConsumingAsync`, `StopConsumingAsync`, `IsConsuming`, `RequestTimeoutAsync`. Use the exact signatures from the source, including `where T : Message` constraints and optional parameters with their defaults.
- **Required `<Aside type="caution">`** on `StopConsumingAsync`: "Stop is terminal" — the source explicitly documents that a stopped bus cannot be restarted. Use that exact wording.
- **Required `<Aside type="note">`** on `RequestTimeoutAsync`: note that not all `IBus` implementations support it; the default implementation throws `NotSupportedException`.
- **Usage — two scenarios:**
  1. Publishing a domain event (`OrderService` publishes `OrderPlaced`).
  2. Request/reply with a single respondent (`ShippingSaga` uses `SendRequestAsync<QuoteShipping, ShippingQuote>`).
- **See also:** `The Bus` (Learn), `Pub/Sub` (Learn), `Request/Reply` (Learn), `IBusConfiguration` (reference), `AddServiceConnect` (reference).

- [ ] **Step 2: Author `ibusconfiguration.mdx`**

Read `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`. Create `website/src/content/docs/reference/bus/ibusconfiguration.mdx` using the primary API template. Specifics:

- **Overview:** the configuration surface passed to the `AddServiceConnect(b => ...)` delegate; aggregates transport, queue, persistence, and pipeline sub-configurations. Link to `/ServiceConnect-CSharp/learn/operations/configuration/`.
- **Reference:** one h3 per public member on `IBusConfiguration` (properties and methods). Use exact signatures from source.
- **Usage — one scenario:** configuring a bus that publishes to RabbitMQ with a named queue.
- **See also:** `Configuration` (Learn), `ITransportConfiguration` (reference), `IQueueConfiguration` (reference), `IPersistenceConfiguration` (reference), `IPipelineConfiguration` (reference).

- [ ] **Step 3: Author `add-serviceconnect.mdx`**

Locate the `AddServiceConnect` extension method. Run `grep -rn "AddServiceConnect" src/ServiceConnect/` to find its file. Read the file, then create `website/src/content/docs/reference/bus/add-serviceconnect.mdx` using the primary API template. Specifics:

- **Title frontmatter:** `AddServiceConnect`.
- **Overview:** the DI entry point — registers `IBus` and its configuration with `IServiceCollection`; callers pass a builder delegate that receives `IBusConfiguration`. Link to `/ServiceConnect-CSharp/learn/getting-started/`.
- **Reference — one h3 per overload** of `AddServiceConnect`. Use exact signatures from source.
- **Usage — two scenarios:**
  1. Minimal: `services.AddServiceConnect(b => b.UseRabbitMQ(t => t.Host = "localhost"));`
  2. With persistence: adding MongoDB-backed process manager storage.
- **See also:** `Getting Started` (Learn), `The Bus` (Learn), `IBusConfiguration` (reference), `IBus` (reference).

- [ ] **Step 4: Populate the Bus sidebar group**

Edit `website/astro.config.mjs`. Locate the empty `Bus` group `{ label: 'Bus', items: [] }` and replace with:

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

- [ ] **Step 5: Verify build**

```bash
cd website && npm run build
```

Expected: build completes; three new sidebar entries render under `Bus`; clicking each renders the page with all four sections. No `Invalid link` warnings.

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/bus/ website/astro.config.mjs
git commit -m "docs(reference): author Bus group — IBus, IBusConfiguration, AddServiceConnect"
```

---

## Task 4: Messages group — Message, Envelope, options

**Files:**
- Read: `src/ServiceConnect.Interfaces/Messages/Message.cs`, `src/ServiceConnect.Interfaces/Messages/Envelope.cs`, `src/ServiceConnect.Interfaces/Options/PublishOptions.cs`, `src/ServiceConnect.Interfaces/Options/SendOptions.cs`, `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`
- Create: `website/src/content/docs/reference/messages/message.mdx`
- Create: `website/src/content/docs/reference/messages/envelope.mdx`
- Create: `website/src/content/docs/reference/messages/options.mdx`
- Modify: `website/astro.config.mjs` (populate `Messages` sidebar group)

- [ ] **Step 1: Author `message.mdx`**

Read `src/ServiceConnect.Interfaces/Messages/Message.cs`. Create `website/src/content/docs/reference/messages/message.mdx` using the primary API template. Specifics:

- **Overview:** the base class every ServiceConnect message inherits. Carries the correlation id used to relate messages across a conversation (request↔reply, command→events, all messages from one process-manager instance). Link to `/ServiceConnect-CSharp/learn/core-concepts/messages/`.
- **Reference:** one h3 for the primary constructor `Message(Guid correlationId)`, one h3 for the `CorrelationId` property.
- **Usage — two scenarios:**
  1. Defining a sealed message type with primary constructor: `OrderPlaced(Guid correlationId) : Message(correlationId)`.
  2. Propagating correlation id from an incoming message to an outgoing event.
- **See also:** `Messages` (Learn), `Envelope` (reference), `Message options` (reference), `IConsumeContext` (reference).

- [ ] **Step 2: Author `envelope.mdx`**

Read `src/ServiceConnect.Interfaces/Messages/Envelope.cs`. Create `website/src/content/docs/reference/messages/envelope.mdx` using the primary API template. Specifics:

- **Overview:** the transport-level wrapper around a serialised message — body bytes plus headers. Consumers rarely construct one directly; most contact is indirect through `IConsumeContext`. Link to `/ServiceConnect-CSharp/learn/core-concepts/messages/`.
- **Reference:** one h3 per public member on `Envelope`.
- **Usage — one scenario:** inspecting an envelope from a custom `IMessageProcessingMiddleware` (headers and raw body).
- **See also:** `Messages` (Learn), `Message` (reference), `IConsumeContext` (reference), `IMessageProcessingMiddleware` (reference).

- [ ] **Step 3: Author `options.mdx`**

Read all three option files. Create `website/src/content/docs/reference/messages/options.mdx` using the primary API template. Because this page covers three types, structure the Reference section with one **h2** per type (`## PublishOptions`, `## SendOptions`, `## RequestOptions`), then h3 per member within each.

Specifics:

- **Title frontmatter:** `Message options`.
- **Overview:** the per-call overrides passed to `IBus.PublishAsync`, `IBus.SendAsync`, and `IBus.SendRequestAsync` respectively. Each is optional; defaults come from `IBusConfiguration`. Link to `/ServiceConnect-CSharp/learn/operations/configuration/`.
- **Reference:** for each type, cover every public property with its default value if any.
- **Usage — three short scenarios:**
  1. Publishing with custom headers via `PublishOptions.Headers`.
  2. Sending to an explicit endpoint override via `SendOptions.Endpoint`.
  3. Setting a request timeout via `RequestOptions.Timeout`.
- **See also:** `Configuration` (Learn), `IBus` (reference).
- **Required `<Aside type="note">`** somewhere in `RequestOptions` section if the source documents `ExpectedReplyCount` semantics (recent commit `a12fcb0` added docs for this — use that as your guide).

- [ ] **Step 4: Populate the Messages sidebar group**

Edit `website/astro.config.mjs`. Locate the empty `Messages` group and replace with:

```js
{
  label: 'Messages',
  items: [
    { label: 'Message', link: '/reference/messages/message/' },
    { label: 'Envelope', link: '/reference/messages/envelope/' },
    { label: 'Message options', link: '/reference/messages/options/' },
  ],
},
```

- [ ] **Step 5: Verify build**

```bash
cd website && npm run build
```

Expected: three new sidebar entries under `Messages`; each page renders cleanly.

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/messages/ website/astro.config.mjs
git commit -m "docs(reference): author Messages group — Message, Envelope, options"
```

---

## Task 5: Handlers group — IMessageHandler, IStreamHandler, IConsumeContext, event-args

**Files:**
- Read: `src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs`, `IStreamHandler.cs`, `src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs`, `src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs`, `ConsumeEventResult.cs`, `PublishEventArgs.cs`, `SendEventArgs.cs`, `OutgoingEventArgs.cs`
- Create: `website/src/content/docs/reference/handlers/imessagehandler.mdx`
- Create: `website/src/content/docs/reference/handlers/istreamhandler.mdx`
- Create: `website/src/content/docs/reference/handlers/iconsumecontext.mdx`
- Create: `website/src/content/docs/reference/handlers/event-args.mdx`
- Modify: `website/astro.config.mjs` (populate `Handlers` sidebar group)

- [ ] **Step 1: Author `imessagehandler.mdx`**

Read `IMessageHandler.cs`. Create the page using the primary API template. Specifics:

- **Title frontmatter:** `IMessageHandler<T>`.
- **Overview:** the contract for consuming a single message type. One method: `HandleAsync`. Implementors are discovered via DI and invoked per message. Link to `/ServiceConnect-CSharp/learn/core-concepts/handlers/`.
- **Reference:** h3 for `HandleAsync` with its signature and `where T : Message` constraint.
- **Usage — two scenarios:**
  1. Idempotent handler: `OrderPlacedHandler` writing to a database with a dedup key.
  2. Handler that publishes a follow-up event using `IBus` from constructor injection.
- **See also:** `Handlers` (Learn), `IConsumeContext` (reference), `IStreamHandler` (reference).

- [ ] **Step 2: Author `istreamhandler.mdx`**

Read `IStreamHandler.cs`. Create the page using the primary API template. Specifics:

- **Overview:** the contract for consuming a streaming message sent via `IBus.CreateStream<T>()`. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/streaming/`.
- **Reference:** one h3 per public member on `IStreamHandler`.
- **Usage — one scenario:** a consumer writing an uploaded file to disk in chunks.
- **See also:** `Streaming` (Learn), `IMessageHandler<T>` (reference), `IBus.CreateStream` (reference — anchor link to `/reference/bus/ibus/#createstream`).

- [ ] **Step 3: Author `iconsumecontext.mdx`**

Read `IConsumeContext.cs`. Create the page using the primary API template. Specifics:

- **Overview:** the ambient context injected into a handler per message — exposes headers, envelope, reply target, bus access for publishing follow-ups. Link to `/ServiceConnect-CSharp/learn/core-concepts/handlers/`.
- **Reference:** one h3 per public member.
- **Usage — one scenario:** a handler reading a correlation-related header and replying via `ctx.ReplyAsync(...)`.
- **See also:** `Handlers` (Learn), `Envelope` (reference), `Message` (reference).

- [ ] **Step 4: Author `event-args.mdx`**

Read all five event-args files. Create the page using the primary API template. Structure: one **h2** per type, h3 per member within.

Specifics:

- **Title frontmatter:** `Consume and outgoing event args`.
- **Overview:** the event-argument DTOs raised by the bus for observers (telemetry, custom logging, diagnostics). Covers `ConsumeEventArgs`, `ConsumeEventResult`, `PublishEventArgs`, `SendEventArgs`, `OutgoingEventArgs`. Link to `/ServiceConnect-CSharp/learn/operations/observability/`.
- **Reference:** h2 per type, h3 per property.
- **Usage — one scenario:** a custom `IMessageProcessingMiddleware` that records a span per message using the consume event data.
- **See also:** `Observability` (Learn), `IMessageProcessingMiddleware` (reference), `ISendMessageMiddleware` (reference).

- [ ] **Step 5: Populate the Handlers sidebar group**

```js
{
  label: 'Handlers',
  items: [
    { label: 'IMessageHandler<T>', link: '/reference/handlers/imessagehandler/' },
    { label: 'IStreamHandler', link: '/reference/handlers/istreamhandler/' },
    { label: 'IConsumeContext', link: '/reference/handlers/iconsumecontext/' },
    { label: 'Event args', link: '/reference/handlers/event-args/' },
  ],
},
```

- [ ] **Step 6: Verify build**

```bash
cd website && npm run build
```

Expected: four new sidebar entries under `Handlers`; all pages render.

- [ ] **Step 7: Commit**

```bash
git add website/src/content/docs/reference/handlers/ website/astro.config.mjs
git commit -m "docs(reference): author Handlers group"
```

---

## Task 6: Configuration group — transport, queue, persistence, pipeline

**Files:**
- Read: `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs`, `IQueueConfiguration.cs`, `IPersistenceConfiguration.cs`, `IPipelineConfiguration.cs`
- Create: four MDX pages under `website/src/content/docs/reference/configuration/`
- Modify: `website/astro.config.mjs` (populate `Configuration` sidebar group)

Each page uses the primary API template. For each, read the corresponding `.cs` file to enumerate members. Page-specific guidance:

- [ ] **Step 1: Author `itransportconfiguration.mdx`**

Specifics:

- **Overview:** the transport-layer configuration — host, port, credentials, TLS, retry, prefetch. The shape differs per transport (RabbitMQ is the only shipped implementation). Link to `/ServiceConnect-CSharp/learn/operations/configuration/`.
- **Reference:** h3 per public member.
- **Usage — one scenario:** configuring a TLS-enabled RabbitMQ connection with a non-default prefetch.
- **See also:** `Configuration` (Learn), `IBusConfiguration` (reference), `IQueueConfiguration` (reference).

- [ ] **Step 2: Author `iqueueconfiguration.mdx`**

Specifics:

- **Overview:** the queue-level configuration — queue name, dead-letter routing, durability, exclusivity. Link to `/ServiceConnect-CSharp/learn/core-concepts/endpoints/`.
- **Reference:** h3 per public member.
- **Usage — one scenario:** declaring a durable queue with a dead-letter exchange.
- **See also:** `Endpoints` (Learn), `ITransportConfiguration` (reference), `IBusConfiguration` (reference).

- [ ] **Step 3: Author `ipersistenceconfiguration.mdx`**

Specifics:

- **Overview:** selects the persistence provider for process managers, aggregators, and timeouts. Shipped providers: InMemory, MongoDB. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/process-manager/`.
- **Reference:** h3 per public member.
- **Usage — two scenarios:**
  1. Development: in-memory persistence.
  2. Production: MongoDB with a named database.
- **See also:** `Process Manager` (Learn), `IAggregatorPersistor` (reference), `IProcessManagerFinder` (reference).

- [ ] **Step 4: Author `ipipelineconfiguration.mdx`**

Specifics:

- **Overview:** wires filters and middleware into the consume and send pipelines. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/filters/`.
- **Reference:** h3 per public member.
- **Usage — one scenario:** adding a logging filter in the consume pipeline and a retry filter in the send pipeline.
- **See also:** `Filters` (Learn), `IFilter` (reference), `IMessageProcessingMiddleware` (reference), `ISendMessageMiddleware` (reference).

- [ ] **Step 5: Populate the Configuration sidebar group**

```js
{
  label: 'Configuration',
  items: [
    { label: 'ITransportConfiguration', link: '/reference/configuration/itransportconfiguration/' },
    { label: 'IQueueConfiguration', link: '/reference/configuration/iqueueconfiguration/' },
    { label: 'IPersistenceConfiguration', link: '/reference/configuration/ipersistenceconfiguration/' },
    { label: 'IPipelineConfiguration', link: '/reference/configuration/ipipelineconfiguration/' },
  ],
},
```

- [ ] **Step 6: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 7: Commit**

```bash
git add website/src/content/docs/reference/configuration/ website/astro.config.mjs
git commit -m "docs(reference): author Configuration group"
```

---

## Task 7: Process Managers group

**Files:**
- Read: `src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs`, `IProcessManagerData.cs`, `IProcessManagerPropertyMapper.cs`, `src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs`, `AggregatorSnapshot.cs`
- Create: four MDX pages under `website/src/content/docs/reference/process-managers/`
- Modify: `website/astro.config.mjs` (populate `Process Managers` sidebar group)

- [ ] **Step 1: Author `iprocesshandler.mdx`**

Specifics:

- **Overview:** the contract for a process-manager step — handles a single message type as part of a longer-running saga; reads and mutates `IProcessManagerData`. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/process-manager/`.
- **Reference:** h3 per public member.
- **Usage — one scenario:** `ShippingSaga` handling `OrderPlaced` by updating saga state and publishing `ShipmentRequested`.
- **See also:** `Process Manager` (Learn), `IProcessManagerData` (reference), `IProcessManagerPropertyMapper` (reference), `IProcessManagerFinder` (reference — extension point).

- [ ] **Step 2: Author `iprocessmanagerdata.mdx`**

Specifics:

- **Overview:** the marker contract for a saga's persisted state. Implementors define properties the framework loads/saves around each step. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/process-manager/`.
- **Reference:** h3 per public member (if any — it's likely a marker with a few required properties).
- **Usage — one scenario:** `ShippingSagaData` with correlation id, order id, state enum.
- **See also:** `Process Manager` (Learn), `IProcessHandler` (reference), `IProcessManagerFinder` (reference).

- [ ] **Step 3: Author `iprocessmanagerpropertymapper.mdx`**

Specifics:

- **Overview:** declares how incoming messages map to saga-data properties — the correlation expression the finder uses to load state. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/process-manager/`.
- **Reference:** h3 per public member.
- **Usage — one scenario:** `ShippingSaga.ConfigureMapping(mapper => mapper.ConfigureMapping<OrderPlaced>(m => m.OrderId).ToSaga(d => d.OrderId));` (match the pattern actually used in code).
- **See also:** `Process Manager` (Learn), `IProcessHandler` (reference).

- [ ] **Step 4: Author `aggregator.mdx`**

Specifics:

- **Title frontmatter:** `Aggregator<T>`.
- **Overview:** groups messages by correlation key and releases them when a count/time condition fires — the Scatter-Gather and fan-in patterns. Covers `Aggregator<T>` and `AggregatorSnapshot` on one page. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/aggregator/`.
- **Reference:** h2 per type, h3 per member within. Use exact signatures.
- **Usage — one scenario:** `QuoteAggregator` collecting `ShippingQuote` replies from multiple carriers and emitting `ShippingQuotesReceived`.
- **See also:** `Aggregator` (Learn), `Scatter-Gather` (Learn), `IAggregatorPersistor` (reference — extension point).

- [ ] **Step 5: Populate the Process Managers sidebar group**

```js
{
  label: 'Process Managers',
  items: [
    { label: 'IProcessHandler', link: '/reference/process-managers/iprocesshandler/' },
    { label: 'IProcessManagerData', link: '/reference/process-managers/iprocessmanagerdata/' },
    { label: 'IProcessManagerPropertyMapper', link: '/reference/process-managers/iprocessmanagerpropertymapper/' },
    { label: 'Aggregator<T>', link: '/reference/process-managers/aggregator/' },
  ],
},
```

- [ ] **Step 6: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 7: Commit**

```bash
git add website/src/content/docs/reference/process-managers/ website/astro.config.mjs
git commit -m "docs(reference): author Process Managers group"
```

---

## Task 8: Filters & Middleware group

**Files:**
- Read: `src/ServiceConnect.Interfaces/Pipelines/IFilter.cs`, `IFilterPipeline.cs`, `IMessageProcessingMiddleware.cs`, `ISendMessageMiddleware.cs`, `ISendMessagePipeline.cs`
- Create: three MDX pages under `website/src/content/docs/reference/filters/`
- Modify: `website/astro.config.mjs` (populate `Filters & Middleware` sidebar group)

- [ ] **Step 1: Author `ifilter.mdx`**

Specifics:

- **Title frontmatter:** `IFilter`.
- **Overview:** a short-circuit stage in the consume pipeline — returns `true` to continue, `false` to drop. Covers both `IFilter` and `IFilterPipeline` (the host that composes them) on one page. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/filters/`.
- **Reference:** h2 per type, h3 per member within.
- **Usage — one scenario:** a duplicate-detection filter that drops messages whose id has been seen.
- **See also:** `Filters` (Learn), `IMessageProcessingMiddleware` (reference), `IPipelineConfiguration` (reference).

- [ ] **Step 2: Author `imessageprocessingmiddleware.mdx`**

Specifics:

- **Overview:** wraps the consume pipeline — observe and/or transform each incoming message. Good fit for telemetry, logging, retry, and outbox patterns. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/filters/`.
- **Reference:** h3 per public member.
- **Usage — one scenario:** recording an activity span per consumed message.
- **See also:** `Filters` (Learn), `Observability` (Learn), `ISendMessageMiddleware` (reference), `IFilter` (reference).

- [ ] **Step 3: Author `isendmessagemiddleware.mdx`**

Specifics:

- **Title frontmatter:** `ISendMessageMiddleware`.
- **Overview:** the outbound counterpart to `IMessageProcessingMiddleware` — wraps each publish/send. Covers both `ISendMessageMiddleware` and `ISendMessagePipeline`. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/filters/`.
- **Reference:** h2 per type, h3 per member within.
- **Usage — one scenario:** stamping an outbound correlation header before publish.
- **See also:** `Filters` (Learn), `IMessageProcessingMiddleware` (reference), `IPipelineConfiguration` (reference).

- [ ] **Step 4: Populate the Filters & Middleware sidebar group**

```js
{
  label: 'Filters & Middleware',
  items: [
    { label: 'IFilter', link: '/reference/filters/ifilter/' },
    { label: 'IMessageProcessingMiddleware', link: '/reference/filters/imessageprocessingmiddleware/' },
    { label: 'ISendMessageMiddleware', link: '/reference/filters/isendmessagemiddleware/' },
  ],
},
```

- [ ] **Step 5: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/filters/ website/astro.config.mjs
git commit -m "docs(reference): author Filters & Middleware group"
```

---

## Task 9: Extension Points — Persistence

**Files:**
- Read: `src/ServiceConnect.Interfaces/Aggregation/IAggregatorPersistor.cs`, `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerFinder.cs`, `src/ServiceConnect.Interfaces/Timeouts/ILeaseAwareTimeoutStore.cs` (if path differs, grep for the type name)
- Create: three MDX pages under `website/src/content/docs/reference/extension-points/persistence/`
- Modify: `website/astro.config.mjs` (populate `Persistence` sidebar group)

Each page uses the **extension-points template** — same sections plus an `## Implementing` block between Reference and Usage.

- [ ] **Step 1: Author `iaggregatorpersistor.mdx`**

Specifics:

- **Overview:** the contract a custom aggregator persistence store must satisfy — snapshot load/save by correlation id. Shipped by default against InMemory and MongoDB; replace when wiring a new database. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/aggregator/`.
- **Reference:** h3 per public member.
- **Implementing:** call out threading (invoked concurrently from multiple consumers; implementations must be safe for concurrent access to different correlation ids), transactional behaviour (what happens if save fails mid-flight), and whether snapshots are expected to be merged or overwritten. Show a skeletal implementation.
- **Usage — one scenario:** a Postgres-backed implementation that uses an `UPSERT` keyed by correlation id.
- **See also:** `Aggregator` (Learn), `Aggregator<T>` (reference), `IPersistenceConfiguration` (reference).

- [ ] **Step 2: Author `iprocessmanagerfinder.mdx`**

Specifics:

- **Overview:** the contract a custom process-manager persistence store must satisfy — load, insert, update, delete saga state by correlation id. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/process-manager/`.
- **Reference:** h3 per public member.
- **Implementing:** stress the concurrency contract (multiple handlers may invoke `FindData` for the same key; implementations should use optimistic concurrency or locking). Document the "not found" contract (return null vs. throw). Show a skeletal implementation.
- **Usage — one scenario:** a Postgres-backed finder using a `version` column for optimistic locking.
- **See also:** `Process Manager` (Learn), `IProcessHandler` (reference), `IProcessManagerData` (reference), `IPersistenceConfiguration` (reference).

- [ ] **Step 3: Author `ileaseawaretimeoutstore.mdx`**

Specifics:

- **Overview:** the contract a timeout store must satisfy, with leasing semantics that let multiple bus instances share a store without duplicate delivery. Link to `/ServiceConnect-CSharp/learn/messaging-patterns/process-manager/` (or a timeouts section if it exists).
- **Reference:** h3 per public member.
- **Implementing:** explain lease expiry, what happens when a consumer crashes mid-delivery, required ordering guarantees.
- **Usage — one scenario:** a Postgres-backed store using a `lease_expires_at` column plus `FOR UPDATE SKIP LOCKED`.
- **See also:** `Process Manager` (Learn), `IProcessManagerFinder` (reference), `IPersistenceConfiguration` (reference).

- [ ] **Step 4: Populate the Persistence sidebar group**

```js
{
  label: 'Persistence',
  items: [
    { label: 'IAggregatorPersistor', link: '/reference/extension-points/persistence/iaggregatorpersistor/' },
    { label: 'IProcessManagerFinder', link: '/reference/extension-points/persistence/iprocessmanagerfinder/' },
    { label: 'ILeaseAwareTimeoutStore', link: '/reference/extension-points/persistence/ileaseawaretimeoutstore/' },
  ],
},
```

- [ ] **Step 5: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/extension-points/persistence/ website/astro.config.mjs
git commit -m "docs(reference): author Extension Points — Persistence"
```

---

## Task 10: Extension Points — Serialization

**Files:**
- Read: `src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs`, `IMessageTypeRegistry.cs`
- Create: two MDX pages under `website/src/content/docs/reference/extension-points/serialization/`
- Modify: `website/astro.config.mjs` (populate `Serialization` sidebar group)

Each page uses the extension-points template.

- [ ] **Step 1: Author `imessageserializer.mdx`**

Specifics:

- **Overview:** the contract for turning a CLR message object into bytes and back. Shipped default is JSON; replace when wiring a binary protocol (Protobuf, MessagePack) or a custom wire format. Link to `/ServiceConnect-CSharp/learn/core-concepts/messages/`.
- **Reference:** h3 per public member.
- **Implementing:** discuss type-id propagation (the serializer is usually paired with an `IMessageTypeRegistry`), versioning, and error-handling contract (what to throw on a malformed payload). Show a skeletal Protobuf implementation.
- **Usage — one scenario:** registering a Protobuf serializer via `IBusConfiguration`.
- **See also:** `Messages` (Learn), `IMessageTypeRegistry` (reference), `Envelope` (reference).

- [ ] **Step 2: Author `imessagetyperegistry.mdx`**

Specifics:

- **Overview:** resolves a message type id (on the wire) to a CLR `Type`. Replace when the type-id scheme differs from the default (e.g. you use full type names on the wire and want compact integer ids instead). Link to `/ServiceConnect-CSharp/learn/core-concepts/messages/`.
- **Reference:** h3 per public member.
- **Implementing:** discuss registration (startup-time vs. lazy), ambiguity handling (what if a type id resolves to two types), and error-handling contract.
- **Usage — one scenario:** a registry keyed by integer type ids declared via attributes.
- **See also:** `Messages` (Learn), `IMessageSerializer` (reference).

- [ ] **Step 3: Populate the Serialization sidebar group**

```js
{
  label: 'Serialization',
  items: [
    { label: 'IMessageSerializer', link: '/reference/extension-points/serialization/imessageserializer/' },
    { label: 'IMessageTypeRegistry', link: '/reference/extension-points/serialization/imessagetyperegistry/' },
  ],
},
```

- [ ] **Step 4: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 5: Commit**

```bash
git add website/src/content/docs/reference/extension-points/serialization/ website/astro.config.mjs
git commit -m "docs(reference): author Extension Points — Serialization"
```

---

## Task 11: Extension Points — Transport

**Files:**
- Read: `src/ServiceConnect.Interfaces/Bus/IConsumer.cs`, `IProducer.cs`, `src/ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs` (the interface lives near the RabbitMQ client — grep if unsure)
- Create: three MDX pages under `website/src/content/docs/reference/extension-points/transport/`
- Modify: `website/astro.config.mjs` (populate `Transport` sidebar group)

Each page uses the extension-points template.

- [ ] **Step 1: Author `iserviceconnectconnection.mdx`**

Specifics:

- **Overview:** the transport-level connection abstraction — where a custom transport implementation starts. Shipped only for RabbitMQ; implement to add support for other brokers (Azure Service Bus, Kafka, Redis Streams). Link to `/ServiceConnect-CSharp/learn/operations/configuration/`.
- **Reference:** h3 per public member.
- **Implementing:** discuss connection lifecycle (connect/disconnect, reconnection strategy), threading (thread-safety expectations), diagnostics hooks, and how `IConsumer` / `IProducer` are produced from a connection.
- **Usage — one scenario:** skeleton of an Azure Service Bus connection implementation.
- **See also:** `Configuration` (Learn), `IConsumer` (reference), `IProducer` (reference), `ITransportConfiguration` (reference).

- [ ] **Step 2: Author `iconsumer.mdx`**

Specifics:

- **Overview:** the transport-level consumer abstraction — pulls messages from the broker and pushes them into the bus. Implemented once per transport. Link to `/ServiceConnect-CSharp/learn/core-concepts/the-bus/`.
- **Reference:** h3 per public member.
- **Implementing:** discuss delivery guarantees (at-least-once), ack/nack contract, prefetch/flow control, cancellation. Show a skeletal consumer loop.
- **Usage — one scenario:** skeleton of a Kafka consumer using `Confluent.Kafka`.
- **See also:** `The Bus` (Learn), `IServiceConnectConnection` (reference), `IProducer` (reference).

- [ ] **Step 3: Author `iproducer.mdx`**

Specifics:

- **Overview:** the transport-level producer abstraction — writes messages to the broker. Implemented once per transport. Link to `/ServiceConnect-CSharp/learn/core-concepts/the-bus/`.
- **Reference:** h3 per public member.
- **Implementing:** discuss publisher-confirm semantics (when does the task complete), idempotency, retry responsibility (producer vs. caller), and how routing information is passed in.
- **Usage — one scenario:** skeleton of a Kafka producer with idempotent writes enabled.
- **See also:** `The Bus` (Learn), `IServiceConnectConnection` (reference), `IConsumer` (reference).

- [ ] **Step 4: Populate the Transport sidebar group**

```js
{
  label: 'Transport',
  items: [
    { label: 'IServiceConnectConnection', link: '/reference/extension-points/transport/iserviceconnectconnection/' },
    { label: 'IConsumer', link: '/reference/extension-points/transport/iconsumer/' },
    { label: 'IProducer', link: '/reference/extension-points/transport/iproducer/' },
  ],
},
```

- [ ] **Step 5: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/extension-points/transport/ website/astro.config.mjs
git commit -m "docs(reference): author Extension Points — Transport"
```

---

## Task 12: Extension Points — Registry

**Files:**
- Read: `src/ServiceConnect.Interfaces/Handlers/IHandlerRegistry.cs`, `src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs`, `src/ServiceConnect.Interfaces/Handlers/IMessageProcessor.cs`
- Create: three MDX pages under `website/src/content/docs/reference/extension-points/registry/`
- Modify: `website/astro.config.mjs` (populate `Registry` sidebar group)

Each page uses the extension-points template.

- [ ] **Step 1: Author `ihandlerregistry.mdx`**

Specifics:

- **Overview:** discovers and holds references to handler types the bus will invoke. Default implementation walks DI registrations; replace if you want a non-DI discovery mechanism or want to filter which handlers the bus activates at runtime. Link to `/ServiceConnect-CSharp/learn/core-concepts/handlers/`.
- **Reference:** h3 per public member.
- **Implementing:** registration lifecycle (startup-time discovery), key lookup, thread-safety expectations.
- **Usage — one scenario:** a config-file-driven registry that activates handlers based on environment.
- **See also:** `Handlers` (Learn), `IMessageProcessor` (reference), `IMessageDispatcher` (reference).

- [ ] **Step 2: Author `imessagedispatcher.mdx`**

Specifics:

- **Overview:** routes an incoming message to its handler(s). The default walks the `IHandlerRegistry`; replace to implement custom routing (e.g. feature-flag gated handler activation). Link to `/ServiceConnect-CSharp/learn/core-concepts/handlers/`.
- **Reference:** h3 per public member.
- **Implementing:** parallel vs. sequential dispatch, error aggregation across handlers, short-circuit contract.
- **Usage — one scenario:** a dispatcher that runs handlers in parallel but aggregates their exceptions.
- **See also:** `Handlers` (Learn), `IHandlerRegistry` (reference), `IMessageProcessor` (reference).

- [ ] **Step 3: Author `imessageprocessor.mdx`**

Specifics:

- **Overview:** the top-level orchestrator that takes a raw `Envelope`, deserialises, applies the consume pipeline, and dispatches. Replace only when every other extension point is insufficient. Link to `/ServiceConnect-CSharp/learn/operations/error-handling/`.
- **Reference:** h3 per public member.
- **Implementing:** discuss the order of operations (deserialize → filter → dispatch → ack), error-handling contract (what causes a nack vs. a poison-message send), and where observability hooks fire.
- **Usage — one scenario:** a processor that emits a custom activity per phase of processing.
- **See also:** `Error Handling` (Learn), `IMessageDispatcher` (reference), `IHandlerRegistry` (reference), `IMessageProcessingMiddleware` (reference).

- [ ] **Step 4: Populate the Registry sidebar group**

```js
{
  label: 'Registry',
  items: [
    { label: 'IHandlerRegistry', link: '/reference/extension-points/registry/ihandlerregistry/' },
    { label: 'IMessageDispatcher', link: '/reference/extension-points/registry/imessagedispatcher/' },
    { label: 'IMessageProcessor', link: '/reference/extension-points/registry/imessageprocessor/' },
  ],
},
```

- [ ] **Step 5: Verify build**

```bash
cd website && npm run build
```

- [ ] **Step 6: Commit**

```bash
git add website/src/content/docs/reference/extension-points/registry/ website/astro.config.mjs
git commit -m "docs(reference): author Extension Points — Registry"
```

---

## Task 13: Expand landing pages with CardGrid link cards

**Files:**
- Modify: `website/src/content/docs/reference/index.mdx`
- Modify: `website/src/content/docs/reference/extension-points/index.mdx`

Now that every reference page exists, replace the placeholder text on each landing with a `<CardGrid>` of `<LinkCard>` entries.

- [ ] **Step 1: Expand the API landing page**

Rewrite `website/src/content/docs/reference/index.mdx` to:

```mdx
---
title: API Reference
description: Reference documentation for the ServiceConnect public API — every type a consumer calls, implements, or configures.
---

import { CardGrid, LinkCard } from '@astrojs/starlight/components';

const base = import.meta.env.BASE_URL.replace(/\/$/, '');

Reference documentation for the ServiceConnect public API. Every type a consumer calls, implements, or configures has a page here.

Organised into two tiers:

- **API Reference** — the primary consumer surface (this section).
- **[Extension Points](/ServiceConnect-CSharp/reference/extension-points/)** — pluggable internals you only touch when replacing a default.

## Primary API

<CardGrid>
  <LinkCard
    title="Bus"
    href={`${base}/reference/bus/ibus/`}
    description="The runtime API — publish, send, request/reply, lifecycle."
  />
  <LinkCard
    title="Messages"
    href={`${base}/reference/messages/message/`}
    description="Message base class, envelope, per-call options."
  />
  <LinkCard
    title="Handlers"
    href={`${base}/reference/handlers/imessagehandler/`}
    description="Write consumers — IMessageHandler, IStreamHandler, IConsumeContext."
  />
  <LinkCard
    title="Configuration"
    href={`${base}/reference/configuration/itransportconfiguration/`}
    description="Configure transport, queues, persistence, and pipelines."
  />
  <LinkCard
    title="Process Managers"
    href={`${base}/reference/process-managers/iprocesshandler/`}
    description="Sagas and aggregators — long-running, stateful flows."
  />
  <LinkCard
    title="Filters & Middleware"
    href={`${base}/reference/filters/ifilter/`}
    description="Pipeline stages for consume and send."
  />
</CardGrid>

> Want to learn how to use ServiceConnect, not just look up a method? Start with [Getting Started](/ServiceConnect-CSharp/learn/getting-started/).
```

- [ ] **Step 2: Expand the Extension Points landing page**

Rewrite `website/src/content/docs/reference/extension-points/index.mdx` to:

```mdx
---
title: Extension Points
description: Pluggable internals — only touch these when replacing a default implementation.
---

import { CardGrid, LinkCard } from '@astrojs/starlight/components';

const base = import.meta.env.BASE_URL.replace(/\/$/, '');

Extension points are the interfaces the framework calls *into*. You only need this section if you're replacing a default implementation — shipping a custom persistence store, serializer, or transport.

Most consumers never open this section. If you're wiring the bus into a new database or message broker, start here; otherwise the [API Reference](/ServiceConnect-CSharp/reference/) has what you need.

<CardGrid>
  <LinkCard
    title="Persistence"
    href={`${base}/reference/extension-points/persistence/iaggregatorpersistor/`}
    description="Custom stores for aggregators, process managers, and timeouts."
  />
  <LinkCard
    title="Serialization"
    href={`${base}/reference/extension-points/serialization/imessageserializer/`}
    description="Custom message serializers and type-id registries."
  />
  <LinkCard
    title="Transport"
    href={`${base}/reference/extension-points/transport/iserviceconnectconnection/`}
    description="Plug in a new message broker — connections, consumers, producers."
  />
  <LinkCard
    title="Registry"
    href={`${base}/reference/extension-points/registry/ihandlerregistry/`}
    description="Replace handler discovery, dispatch, or top-level processing."
  />
</CardGrid>
```

- [ ] **Step 3: Verify build**

```bash
cd website && npm run build
```

Expected: both landings render with card grids linking into the correct first page of each group. No `Invalid link` warnings.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/reference/index.mdx \
        website/src/content/docs/reference/extension-points/index.mdx
git commit -m "docs(reference): expand landing pages with LinkCard grids"
```

---

## Task 14: Learn cross-links — add Reference sections

**Files:**
- Modify: 13 Learn pages (listed below)

Each affected Learn page gets a `## Reference` section appended at the bottom (after any existing content, before any existing "What next?" / "Try it next" block if present — otherwise at the very end). The section is a bulleted list of markdown links into the new reference pages.

For each page: open it, add the section, preview the rendered page visually or run `npm run build` after all pages are updated.

- [ ] **Step 1: `learn/core-concepts/the-bus.mdx`**

Append:

```mdx
## Reference

- [`IBus`](/ServiceConnect-CSharp/reference/bus/ibus/) — runtime API surface
- [`IBusConfiguration`](/ServiceConnect-CSharp/reference/bus/ibusconfiguration/) — how the bus gets wired
- [`AddServiceConnect`](/ServiceConnect-CSharp/reference/bus/add-serviceconnect/) — DI entry point
```

- [ ] **Step 2: `learn/core-concepts/messages.mdx`**

Append:

```mdx
## Reference

- [`Message`](/ServiceConnect-CSharp/reference/messages/message/) — base class and correlation id
- [`Envelope`](/ServiceConnect-CSharp/reference/messages/envelope/) — transport-level wrapper
- [Message options](/ServiceConnect-CSharp/reference/messages/options/) — `PublishOptions`, `SendOptions`, `RequestOptions`
```

- [ ] **Step 3: `learn/core-concepts/handlers.mdx`**

Append:

```mdx
## Reference

- [`IMessageHandler<T>`](/ServiceConnect-CSharp/reference/handlers/imessagehandler/) — the handler contract
- [`IConsumeContext`](/ServiceConnect-CSharp/reference/handlers/iconsumecontext/) — per-message context
- [`IStreamHandler`](/ServiceConnect-CSharp/reference/handlers/istreamhandler/) — streaming messages
```

- [ ] **Step 4: `learn/core-concepts/endpoints.mdx`**

Append:

```mdx
## Reference

- [`ITransportConfiguration`](/ServiceConnect-CSharp/reference/configuration/itransportconfiguration/) — broker-level config
- [`IQueueConfiguration`](/ServiceConnect-CSharp/reference/configuration/iqueueconfiguration/) — per-queue config
```

- [ ] **Step 5: `learn/messaging-patterns/pub-sub.mdx`**

Append:

```mdx
## Reference

- [`IBus.PublishAsync`](/ServiceConnect-CSharp/reference/bus/ibus/#publishasynct) — the publish method
- [Message options](/ServiceConnect-CSharp/reference/messages/options/) — `PublishOptions` for headers and routing overrides
```

- [ ] **Step 6: `learn/messaging-patterns/point-to-point.mdx`**

Append:

```mdx
## Reference

- [`IBus.SendAsync`](/ServiceConnect-CSharp/reference/bus/ibus/#sendasynct) — the send method
- [Message options](/ServiceConnect-CSharp/reference/messages/options/) — `SendOptions` for endpoint overrides
```

- [ ] **Step 7: `learn/messaging-patterns/request-reply.mdx`**

Append:

```mdx
## Reference

- [`IBus.SendRequestAsync`](/ServiceConnect-CSharp/reference/bus/ibus/#sendrequestasynct-treply) — single-reply request
- [`IBus.SendRequestMultiAsync`](/ServiceConnect-CSharp/reference/bus/ibus/#sendrequestmultiasynct-treply) — multi-reply request
- [Message options](/ServiceConnect-CSharp/reference/messages/options/) — `RequestOptions` for timeouts and expected replies
```

- [ ] **Step 8: `learn/messaging-patterns/process-manager.mdx`**

Append:

```mdx
## Reference

- [`IProcessHandler`](/ServiceConnect-CSharp/reference/process-managers/iprocesshandler/) — saga step contract
- [`IProcessManagerData`](/ServiceConnect-CSharp/reference/process-managers/iprocessmanagerdata/) — saga state marker
- [`IProcessManagerPropertyMapper`](/ServiceConnect-CSharp/reference/process-managers/iprocessmanagerpropertymapper/) — correlation mapping
- [`IProcessManagerFinder`](/ServiceConnect-CSharp/reference/extension-points/persistence/iprocessmanagerfinder/) — extension point for custom persistence
```

- [ ] **Step 9: `learn/messaging-patterns/aggregator.mdx`**

Append:

```mdx
## Reference

- [`Aggregator<T>`](/ServiceConnect-CSharp/reference/process-managers/aggregator/) — aggregator base class and snapshot
- [`IAggregatorPersistor`](/ServiceConnect-CSharp/reference/extension-points/persistence/iaggregatorpersistor/) — extension point for custom persistence
```

- [ ] **Step 10: `learn/messaging-patterns/filters.mdx`**

Append:

```mdx
## Reference

- [`IFilter`](/ServiceConnect-CSharp/reference/filters/ifilter/) — short-circuit consume-pipeline stage
- [`IMessageProcessingMiddleware`](/ServiceConnect-CSharp/reference/filters/imessageprocessingmiddleware/) — wrap the consume pipeline
- [`ISendMessageMiddleware`](/ServiceConnect-CSharp/reference/filters/isendmessagemiddleware/) — wrap the send pipeline
- [`IPipelineConfiguration`](/ServiceConnect-CSharp/reference/configuration/ipipelineconfiguration/) — wiring config
```

- [ ] **Step 11: `learn/messaging-patterns/streaming.mdx`**

Append:

```mdx
## Reference

- [`IStreamHandler`](/ServiceConnect-CSharp/reference/handlers/istreamhandler/) — streaming consumer contract
- [`IBus.CreateStream`](/ServiceConnect-CSharp/reference/bus/ibus/#createstreamt) — streaming producer entry point
```

- [ ] **Step 12: `learn/operations/configuration.mdx`**

Append:

```mdx
## Reference

- [`IBusConfiguration`](/ServiceConnect-CSharp/reference/bus/ibusconfiguration/) — the configure delegate target
- [`ITransportConfiguration`](/ServiceConnect-CSharp/reference/configuration/itransportconfiguration/) — transport-level config
- [`IQueueConfiguration`](/ServiceConnect-CSharp/reference/configuration/iqueueconfiguration/) — per-queue config
- [`IPersistenceConfiguration`](/ServiceConnect-CSharp/reference/configuration/ipersistenceconfiguration/) — persistence provider selection
- [`IPipelineConfiguration`](/ServiceConnect-CSharp/reference/configuration/ipipelineconfiguration/) — pipeline wiring
```

- [ ] **Step 13: Verify build**

```bash
cd website && npm run build
```

Expected: every new anchor link resolves. Anchor slugs follow Starlight's default rule (kebab-case of the heading text). If a reference page's member heading was `### \`PublishAsync<T>\``, the anchor is `#publishasynct`. If your authored pages use slightly different anchors, update the Learn links to match.

- [ ] **Step 14: Commit**

```bash
git add website/src/content/docs/learn/
git commit -m "docs(learn): add Reference sections linking into hand-curated API"
```

---

## Task 15: Final verification

**Files:** none modified — verification only.

- [ ] **Step 1: Full clean build**

```bash
cd website
rm -rf dist .astro
npm run build
```

Expected: clean build with zero `Invalid link` or `Page not found` warnings. Note any warnings in the output; resolve before marking this task complete.

- [ ] **Step 2: Visual spot-check of five pages**

Start the dev server:

```bash
cd website && npm run dev
```

Then open each of these in a browser and confirm the page renders, all four (or five, for extension-point pages) sections are present, and sidebar navigation works:

1. `http://localhost:4321/ServiceConnect-CSharp/reference/` — API landing with card grid
2. `http://localhost:4321/ServiceConnect-CSharp/reference/bus/ibus/` — IBus (the exemplar)
3. `http://localhost:4321/ServiceConnect-CSharp/reference/extension-points/` — Extension Points landing
4. `http://localhost:4321/ServiceConnect-CSharp/reference/extension-points/persistence/iaggregatorpersistor/` — an extension-point page (check `## Implementing` section present)
5. `http://localhost:4321/ServiceConnect-CSharp/learn/core-concepts/the-bus/` — a Learn page with the new Reference section at the bottom

- [ ] **Step 3: Cross-link sample**

From the IBus page, click the "See also" link to `IBusConfiguration`. Confirm it navigates correctly. From `IBusConfiguration`, click back to `IBus`. Confirm the anchor links in Learn pages (e.g. `ibus/#publishasynct`) scroll to the correct member heading.

- [ ] **Step 4: Confirm DocFX is gone**

```bash
ls website/docfx 2>/dev/null && echo "FAIL: docfx dir still present"
ls website/_docfx_metadata 2>/dev/null && echo "FAIL: metadata dir still present"
ls website/public/api 2>/dev/null && echo "FAIL: api dir still present"
ls .config/dotnet-tools.json 2>/dev/null && echo "FAIL: tools manifest still present"
```

Expected: no `FAIL` lines; all four listings return non-zero exit codes.

- [ ] **Step 5: No stale `/api/` references**

```bash
grep -r "api/index.html" website/src/ 2>/dev/null && echo "FAIL: stale /api/ link"
grep -r "/api/" website/src/ 2>/dev/null | grep -v "reference/" && echo "FAIL: stale /api/ link" || true
```

Expected: no `FAIL` lines.

- [ ] **Step 6: Commit nothing, close out**

No commit needed — this task is verification-only. If any step failed, fix in a new targeted commit before marking complete.
