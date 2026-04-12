 Finding | Reason |
|---------|--------|
| R-009 (Service Locator) | Inherent to message dispatch design; full refactor requires interface changes |
| R-010 (Layering violation) | Requires moving `IMessageTypeRegistry` to Interfaces; large project structure change |
| R-011/R-012/R-013 (ISP violations) | Interface segregation requires breaking API changes; separate future work |
| R-016 (Missing CancellationToken) | Requires changing all public async APIs — breaking change; needs major version bump |
| R-017/R-018 (Dedup persistor catch-and-swallow) | In filter project using Common.Logging; async fix requires IFilter interface change |
| R-020/R-021 (SRP: ProcessManagerProcessor, Client) | Large refactors that change internal structure; separate future work |
| R-022 (Dedup filter combinatorial explosion) | Major filter project restructuring; separate future work |
| R-027 (MongoDbSsl manual parsing) | Requires full rewrite of MongoDB SSL dedup filter; separate future work |
| R-028 (Zero unit test coverage) | Large effort; separate future work focused on test coverage |
| R-032 (DeduplicationFilterSettings singleton) | Requires DI migration of filter project; separate future work |
| R-034 (Bus.StartConsumingAsync race) | Mitigated by local consumer copy; full fix requires CancellationToken support (R-016) |
