You are a senior .NET performance engineer conducting a deep performance audit of this C# solution. Work through the entire codebase methodically using the two phases below.

---

## PHASE 1 — PERFORMANCE AUDIT

Scan every project, file, and class in this solution. For each issue found, record:
- **File & location** (file path, class, method, line range)
- **Category** (from the checklist below)
- **Severity** (Critical / High / Medium / Low)
- **Current behavior** and why it's a problem
- **Estimated impact** (CPU time, memory, GC pressure, I/O wait, throughput)

### Audit Checklist — examine every item exhaustively:

**Memory & GC Pressure**
- Unnecessary heap allocations in hot paths (object creation inside loops, excessive string concatenation, LINQ in tight loops)
- Missing use of `Span<T>`, `Memory<T>`, `stackalloc`, or `ArrayPool<T>` where buffers are repeatedly allocated
- Large Object Heap (LOH) allocations that could be avoided or pooled
- Value types boxed unnecessarily (e.g., passing structs through `object` or non-generic interfaces)
- Finalizers or IDisposable implementations that are missing, incorrect, or holding resources too long
- Excessive closure captures creating hidden allocations
- String handling: repeated concatenation instead of `StringBuilder`, missing `string.Create`, unnecessary `ToString()` calls, missing `StringComparison.Ordinal` on comparisons

**LINQ & Collections**
- LINQ chains that cause multiple enumerations of the same source
- LINQ used where a simple `for`/`foreach` loop would eliminate allocations
- Wrong collection type for the access pattern (e.g., `List<T>` used for frequent lookups instead of `Dictionary` or `HashSet`)
- Missing initial capacity on `List<T>`, `Dictionary<TKey,TValue>`, `StringBuilder`
- `ToList()` / `ToArray()` called unnecessarily or prematurely, materializing data that could stay lazy
- Missing use of `Collection­sMarshal.GetValueRefOrAddDefault` or `CollectionsMarshal.AsSpan` for high-throughput dictionary/list access

**Async, Concurrency & I/O**
- Sync-over-async (`Task.Result`, `.Wait()`, `.GetAwaiter().GetResult()` in non-startup code)
- Async-over-sync (wrapping synchronous work in `Task.Run` for no reason)
- Missing `ConfigureAwait(false)` in library code
- Missing cancellation token propagation
- `async void` methods outside of event handlers
- Unbatched I/O: multiple sequential DB/HTTP/file calls that could be parallelized or batched
- Missing use of `IAsyncEnumerable<T>` for streaming large result sets
- Thread pool starvation risks from blocking calls
- Locks held across await points or overly coarse locking; candidates for `SemaphoreSlim`, `Channel<T>`, or lock-free patterns

**Database & ORM (Entity Framework / Dapper / ADO.NET)**
- N+1 query patterns (lazy loading pulling queries inside loops)
- Missing `AsNoTracking()` for read-only queries
- Over-fetching columns (`SELECT *` behavior) when projection would suffice
- Missing compiled queries for hot paths
- Unbounded result sets without pagination
- Missing indexes suggested by query patterns
- Connection pool exhaustion risks (connections not disposed, long-lived transactions)
- Raw SQL or stored proc candidates where ORM overhead is measurable

**HTTP & Serialization**
- `HttpClient` created per-request instead of using `IHttpClientFactory`
- Missing response streaming for large payloads
- Synchronous serialization/deserialization of large objects; missing `System.Text.Json` source generators
- Over-serialization (serializing entire object graphs when subsets are needed)
- Missing HTTP caching, compression, or conditional request headers

**Caching & Computation**
- Repeated expensive computations that could be cached (`IMemoryCache`, `Lazy<T>`, memoization)
- Missing distributed cache usage for shared state
- Cache key collisions or missing eviction policies
- Expensive reflection that could be replaced with source generators or cached delegates

**Architecture & Design Patterns**
- Unnecessary middleware or filters running on every request
- DI registrations with wrong lifetimes (transient services that should be scoped/singleton, or singletons capturing scoped dependencies)
- Excessive logging in hot paths (string interpolation evaluated even when log level is disabled)
- Configuration values read repeatedly instead of bound once via `IOptions<T>`

**Low-Level / Runtime**
- Missing `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on small, hot helper methods
- Missed opportunity for `ref struct`, `in` parameters, `ref returns` to avoid copying large structs
- Regular expressions not compiled or not using source-generated `[GeneratedRegex]`
- Missing `frozen` collections (`FrozenDictionary`, `FrozenSet`) for read-heavy lookup tables built once

---

After completing the full audit, write the report to a markdown document in the docs directory

---

Begin Phase 1 now. Read the full solution structure, then systematically audit every file.