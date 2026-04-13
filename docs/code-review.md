You are a senior .NET architect performing an exhaustive code review of the C# solution at src/ServiceConnect.sln. Your goal is to identify every issue — from critical bugs to subtle design smells — fix them all, verify the fixes with tests, and repeat until the codebase is clean.

## PHASE 1 — DISCOVERY (use sub-agents for each review area)

Spawn a separate sub-agent for EACH of the following review areas. Each sub-agent must produce a structured findings report in markdown with severity (Critical / High / Medium / Low / Info), file path, line number(s), description, and a recommended fix.

### Sub-Agent 1: Bugs, Defects & Correctness
- Null reference risks, unhandled exceptions, race conditions, deadlocks
- Off-by-one errors, incorrect boundary checks, silent failures
- Incorrect LINQ usage (deferred execution pitfalls, multiple enumeration)
- Async/await misuse: fire-and-forget, sync-over-async, missing ConfigureAwait where appropriate
- Improper disposal of IDisposable resources
- Incorrect equality comparisons (reference vs value)
- Thread-safety issues with shared mutable state
- Flawed conditional logic or unreachable code paths

### Sub-Agent 2: Architecture & Design
- Violation of SOLID principles (identify each violation specifically)
  - Single Responsibility: classes or methods doing too much
  - Open/Closed: types that require modification instead of extension
  - Liskov Substitution: subtypes that break parent contracts
  - Interface Segregation: fat interfaces forcing unused implementations
  - Dependency Inversion: high-level modules depending on concretions
- Layering violations (e.g., UI referencing data access directly, domain depending on infrastructure)
- Circular dependencies between projects or namespaces
- Misuse of design patterns or unnecessary pattern complexity
- Missing abstractions or God classes
- Inappropriate coupling between modules
- Evaluate the project/folder structure — is it intuitive and navigable for a new developer?

### Sub-Agent 3: CLEAN Code Principles
- **C**ohesion — are classes and methods focused on a single concept?
- **L**oosely coupled — can components be changed independently?
- **E**ncapsulated — is internal state properly hidden? Are there public fields that should be properties? Are mutable collections exposed?
- **A**ssertive — do objects manage their own data rather than relying on external logic?
- **N**on-redundant — is there duplicated logic, copy-paste code, or repeated patterns that should be abstracted?
- Method length: flag any method over 20 lines and evaluate if it should be decomposed
- Class length: flag any class over 300 lines
- Nesting depth: flag any block nested more than 3 levels deep
- Naming: are names intention-revealing, consistent, and free of abbreviations or encodings?
- Comments: are there comments that compensate for unclear code instead of the code being rewritten to be self-explanatory?

### Sub-Agent 4: C# / .NET Bad Practices
- String concatenation in loops instead of StringBuilder
- Throwing `Exception` or `SystemException` instead of specific exception types
- Catching generic `Exception` without good reason
- Empty catch blocks or catch-and-swallow patterns
- Using `magic strings` or `magic numbers` instead of constants/enums
- Mutable statics or ambient context (ServiceLocator, static gateways)
- Improper use of `Task.Result` or `Task.Wait()` (sync-over-async)
- Not using `CancellationToken` where appropriate
- Hardcoded configuration values instead of IOptions/IConfiguration
- Missing or incorrect `sealed` on classes not designed for inheritance
- Using `DateTime.Now` instead of injecting a time abstraction
- Missing `readonly` on fields that never change after construction
- Using `public` access where `internal` or `private` would suffice
- Returning `null` from collections instead of empty collections
- Not leveraging modern C# features where they improve clarity (pattern matching, records, file-scoped namespaces, raw string literals, collection expressions, etc.)

### Sub-Agent 5: Technical Debt & Maintainability
- TODO/HACK/FIXME comments — catalogue every one with context
- Dead code: unused classes, methods, parameters, using directives, variables
- Inconsistent patterns across the codebase (e.g., some services use repository pattern, others hit the DB directly)
- Missing XML documentation on public APIs
- Inconsistent error handling strategies across layers
- Missing or weak input validation
- Hardcoded values that should be configurable
- Test gaps: identify public methods and critical paths that lack test coverage
- Fragile tests: tests coupled to implementation details instead of behaviour
- Missing integration or edge-case tests

### Sub-Agent 6: Security
- SQL injection vectors (raw string queries)
- XSS vulnerabilities in any web output
- Secrets or connection strings hardcoded in source
- Missing authorization checks
- Insecure deserialization
- Overly permissive CORS policies
- Missing input sanitization on public endpoints
- Logging sensitive data (PII, tokens, passwords)

## PHASE 2 — CONSOLIDATE

After all sub-agents complete:
1. Merge all findings into a single deduplicated master report
2. Sort by severity (Critical → Info), then by file path
3. Group related findings that should be fixed together
4. Assign a sequential ID to each finding (e.g., R-001, R-002, ...)
5. Write the full report to a markdown file in the docs directory

Begin now. Start Phase 1.