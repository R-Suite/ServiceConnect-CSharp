# Contributing to ServiceConnect

Thanks for your interest in contributing. ServiceConnect is a small, opinionated
async message bus over RabbitMQ for modern .NET. This guide covers how the project
is laid out, how to build and test it, the conventions we hold code to, and how
releases are cut.

For consumer-facing usage, see the [README](README.md) and the
[documentation site](https://r-suite.github.io/ServiceConnect-CSharp/).

## Before you start

- For anything more than a trivial fix, open an issue first so we can agree on the
  approach before you invest time.
- Bug reports are most useful with a minimal reproduction (a failing test or a small
  console app against the `examples/` brokers is ideal).
- By contributing you agree your work is licensed under the project's
  [MIT license](LICENSE.md).

## Prerequisites

- **.NET 8 and .NET 10 SDKs.** The libraries multi-target `net8.0;net10.0`, so both
  are needed for a full build. Test projects target `net10.0` only.
- **Docker.** Required for the end-to-end tests and the stress harness — they spin up
  real RabbitMQ and MongoDB containers via [Testcontainers](https://testcontainers.com/).
- **Node.js 22** — only if you're working on the documentation site under [`website/`](website).

## Project layout

The solution is [`src/ServiceConnect.slnx`](src/ServiceConnect.slnx). It follows a
clean-architecture layering where dependencies point inward toward the abstractions:

| Project | Role |
| --- | --- |
| `ServiceConnect.Interfaces` | Public abstractions — `IBus`, message contracts, options, and the exception hierarchy. Depends on nothing but the BCL. |
| `ServiceConnect` | Core runtime — dispatch pipeline, handler discovery, process managers, aggregators, request/reply. Depends only on `Interfaces` + `Microsoft.Extensions.*` abstractions. |
| `ServiceConnect.Client.RabbitMQ` | RabbitMQ transport. |
| `ServiceConnect.Persistence.InMemory` / `ServiceConnect.Persistence.MongoDb` | Process-manager / aggregator / timeout persistence. |
| `ServiceConnect.Telemetry` | OpenTelemetry tracing (W3C `traceparent`, OTel messaging semconv). |
| `ServiceConnect.HealthChecks` | `Microsoft.Extensions.Diagnostics.HealthChecks` integration. |

The concrete transport / persistence / feature modules reference **only**
`ServiceConnect.Interfaces` and the `ServiceConnect` core — never each other.
Keep it that way: new transports or persistence backends are self-contained packages
that depend inward, not sideways.

Other top-level directories:

- [`examples/`](examples) — one runnable console app per messaging pattern, each with a
  `run.sh` and a `docker-compose.yml` for a local broker. New behaviour worth showing
  off should come with (or extend) an example.
- [`examples/StressHarness`](examples/StressHarness) — soak / chaos harness driven by
  [`verify-all.sh`](verify-all.sh).
- [`website/`](website) — the Astro + Starlight documentation site.

## Building and testing

From the repository root:

```bash
# Restore + build the whole solution
dotnet build src/ServiceConnect.slnx -c Release

# Unit tests (fast, fully mocked — no Docker needed)
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -c Release

# End-to-end tests (starts RabbitMQ + MongoDB via Testcontainers — Docker required)
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -c Release
```

[`verify-all.sh`](verify-all.sh) is the full local gate and is what you should run
before opening a PR. It runs the unit tests, the E2E tests, and a soak + chaos run of
the stress harness, tearing down its Docker resources on exit:

```bash
./verify-all.sh
# Skip stages with env flags while iterating, e.g.:
SKIP_HARNESS=1 SKIP_CHAOS=1 ./verify-all.sh
```

The repo's build/test scripts pass `-m:1` to cap MSBuild parallelism; on a
resource-constrained machine you may want to do the same for ad-hoc `dotnet` commands.

`TreatWarningsAsErrors` is on for the whole solution, so **a warning fails the build** —
including analyzer diagnostics and missing XML doc comments on public members. A green
local build means the same checks CI runs have passed.

## Coding conventions

[`.editorconfig`](.editorconfig) is the source of truth for style and is enforced at
build time (`EnforceCodeStyleInBuild`). Run your editor's "format document" / `dotnet
format` before committing. The highlights below are the ones that trip people up.

### Language & structure

- **Multi-targeting:** code compiles under C# 12 for `net8.0` and C# 14 for `net10.0`.
  Anything that uses newer language or BCL features (`System.Threading.Lock`, the
  `field` keyword, …) must be guarded with `#if NET9_0_OR_GREATER` (or similar) and
  compile cleanly under both targets.
- **File-scoped namespaces** (`namespace ServiceConnect;`). Namespaces follow the
  public-API shape, not the folder layout — folders are for navigation only.
- **`sealed` by default.** Seal classes unless they're explicitly designed for
  extension.
- **Primary constructors** where they read well; **`using` directives outside the
  namespace**, `System.*` sorted first.
- **Nullable reference types are enabled** everywhere — annotate accordingly and guard
  public entry points with `ArgumentNullException.ThrowIfNull(...)`.

### Async

- Every public async method takes a `CancellationToken cancellationToken = default`
  (defaulted) and threads it through.
- Async methods carry the `Async` suffix.
- Library code uses `.ConfigureAwait(false)` consistently on every `await`. (Test code
  doesn't need it — xUnit has no synchronization context.)

### Naming

- Interfaces are `I`-prefixed, type parameters `T`-prefixed, constants and
  `private static readonly` / `const` fields are `PascalCase`, and other private
  fields are `_camelCase`.

### Logging, DI, exceptions, docs

- **Logging** goes through `Microsoft.Extensions.Logging.ILogger<T>` using
  source-generated `[LoggerMessage]` methods, not interpolated `Log*` calls.
- **DI** is exposed through the `AddServiceConnect(...)` extension and the
  `ServiceConnectBuilder` fluent API — register new components there rather than
  expecting consumers to wire concrete types.
- **Exceptions** derive from the `ServiceConnectException` base (e.g.
  `TransportException`, `PersistenceException`, `RequestTimeoutException`,
  `OutgoingFiltersBlockedException`). Add a sealed, well-named subtype rather than
  throwing bare `Exception`.
- **Public APIs are XML-documented.** Missing `<summary>` on a public member fails the
  build.

### Comments

Comments describe the implementation: what the code does, the invariant it preserves,
the trade-off it expresses, the *why* behind a non-obvious choice. They must **not**
carry meta-references that rot — issue/ticket IDs, phase or work-stream labels, commit
hashes, or "fixed in X" framing. That history belongs in the commit message and PR
description; in-source comments should read as if the current shape was always the
design.

## Tests

- **xUnit** with **Moq** for mocking and plain `Assert.*` assertions (no Fluent
  Assertions).
- Name tests `Subject_Condition_ExpectedResult`, e.g.
  `PublishAsync_EnvelopeDoesNotContainMessageTypeKey`,
  `SendToManyAsync_AllSucceed_NoException`.
- **Unit tests** mock all external dependencies and need no Docker. **E2E tests** use
  Testcontainers and exercise a real broker + database over the wire.
- Use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) to control time —
  **don't** use `Task.Delay` / `Thread.Sleep` to coordinate timing-sensitive tests.
- `ServiceConnect.SerializationCompatTests` guards wire-format backward compatibility
  (Newtonsoft.Json ↔ System.Text.Json round-trips). If you touch serialization, run it
  and don't break the corpus.
- New features need tests; bug fixes should come with a regression test that fails
  before the fix.

## Documentation

User-facing docs live in [`website/`](website) (Astro + Starlight) and deploy to GitHub
Pages from `master`. If your change alters public API or behaviour, update the relevant
pages. Build the site locally with `npm ci && npm run build` in `website/`.

## Commits and pull requests

- **Branch from `master`.** CI runs on every branch and every PR, so push early to get
  feedback.
- Follow **Conventional Commits** for messages — `type(scope): summary`, e.g.
  `fix(rabbitmq): …`, `docs: …`, `ci: …`. Common types: `feat`, `fix`, `docs`, `test`,
  `refactor`, `ci`, `chore`. Put ticket references and detailed rationale in the commit
  body / PR description, not in code comments.
- Keep PRs focused. Make sure `./verify-all.sh` (or at least the relevant test
  projects) and a Release build pass before requesting review.

## Releasing (maintainers)

Releases are cut from **`master` only** and driven entirely by a git tag. The base
version lives in [`src/Directory.Build.props`](src/Directory.Build.props) (`<Version>`);
the tag drives the published package version, and pushing a `v*` tag triggers the
[release workflow](.github/workflows/release.yml).

Two guards run before anything is built or pushed:

- **On master** — the tagged commit must be reachable from `origin/master`, so tag
  *after* your release commit is merged.
- **Base matches props** — the tag's base version (minus any pre-release suffix) must
  equal `<Version>`; a mismatch fails the run.

### Stable release

1. Bump `<Version>` in `src/Directory.Build.props` (e.g. `7.1.0`), commit, and merge to
   `master`.
2. Tag the merged commit and push the tag:

   ```bash
   git checkout master && git pull
   git tag -a v7.1.0 -m "Release 7.1.0"
   git push origin v7.1.0
   ```

### Pre-release

Pre-releases are also cut from `master`. A SemVer pre-release suffix (`-beta.1`,
`-rc.1`, …) shares the same base version, so no props change is needed between a
pre-release and its stable cut — bump `<Version>` only when moving to the next base:

```bash
# <Version> is already 7.1.0 on master
git tag -a v7.1.0-rc.1 -m "7.1.0 RC1" && git push origin v7.1.0-rc.1   # pre-release
# ...validate, then promote the same base to stable:
git tag -a v7.1.0      -m "Release 7.1.0" && git push origin v7.1.0     # stable
```

NuGet flags any hyphenated version as a pre-release and hides it from default installs;
consumers opt in with `dotnet add package ServiceConnect --prerelease`.

### Notes

- **Push the tag explicitly** — a plain `git push` does not send tags, and avoid
  `git push --tags` (it pushes every local tag).
- **Use dot-numeric suffixes** — `-rc.1`, `-rc.2`, `-rc.10` sort correctly; `-rc1` /
  `-rc10` sort as strings (so `rc10` < `rc2`).
- **Versions are permanent** — NuGet packages can be delisted but not unpublished, so a
  pushed tag's version is effectively final. The release push uses `--skip-duplicate`,
  so re-running after a partial failure is safe.
- **Manual dispatch** — the workflow can also be run from *Actions → Release to NuGet*
  with a `version` input; it's subject to the same two guards (run it from `master`).
