# ServiceConnect v7 Breaking Changes

This file tracks all breaking API changes introduced on the `v7-clean-architecture` branch.

---

- `Aggregator<T>.Execute(IList<T>)` → `Aggregator<T>.ExecuteAsync(IList<T>, CancellationToken)` returning `Task`. Implementors must change the method signature and return `Task.CompletedTask` (or `await` real work). Fixes sync-over-async footgun.
- `IStreamHandler<T>.Execute(TMessage)` → `IStreamHandler<T>.ExecuteAsync(TMessage, CancellationToken)` returning `Task`. Same pattern.
