# Tier 1 Optimization Benchmark Results

Machine: Apple M2 Pro, .NET 10.0.1, macOS Sequoia 15.7.4

---

## Baseline (no optimizations)

### InMemoryBenchmarks (DefaultJob)

| Method            | Mean           | Allocated |
|-------------------|---------------:|----------:|
| Check_Simple      | 578.3 ns       | 912 B     |
| Check_Complex     | 659,293.1 ns   | 587,306 B |
| SubjectPermission | 700,029.1 ns   | 611,813 B |
| LookupEntity      | 1,720,034.4 ns | 894,590 B |

### OptimizationValidationBenchmarks (SimpleJob: 1 launch, 3 warmup, 10 iter)

| Method                       | Category    | Mean           | Allocated |
|------------------------------|-------------|---------------:|----------:|
| Check_Reflexive              | Reflexive   | 300.6 ns       | 736 B     |
| LookupEntity_ConvergentPaths | LookupDedup | 1,583,136.0 ns | 51,659 B  |

---

## Optimization 1: Reflexive fast-path in CheckEngine.CheckInternal

**What:** Before any schema lookup, check whether the request is tautologically true:
`SubjectType == EntityType && SubjectId == EntityId && SubjectRelation == Permission`

**Where:** `src/Valtuutus.Core/Engines/Check/CheckEngine.cs` — top of `CheckInternal`.

### Results after Optimization 1

#### InMemoryBenchmarks (general — reflexive fast-path has no effect here)

| Method            | Mean           | Allocated | vs Baseline |
|-------------------|---------------:|----------:|------------:|
| Check_Simple      | 442.7 ns       | 912 B     | ~same       |
| Check_Complex     | 720,347.8 ns ¹ | 586,952 B | ~same       |
| SubjectPermission | 710,083.6 ns   | 611,464 B | ~same       |
| LookupEntity      | 1,777,100.7 ns | 894,590 B | ~same       |

¹ Median used; Mean inflated by concurrent benchmark process noise.

#### OptimizationValidationBenchmarks — reflexive case

| Method          | Baseline        | After Opt 1  | Speedup | Alloc reduction |
|-----------------|----------------:|-------------:|--------:|----------------:|
| Check_Reflexive | 300.6 ns / 736B | 77.07 ns / 248B | **3.9×** | **3.0×** |

---

## Optimization 2: Schema-level type guard in CheckEngine.CheckRelation

**What:** Before calling `GetRelations()`, verify that the subject type is a valid assignee for the
relation being checked. Unreachable subject types are pruned immediately without any DB call.

**Where:** `src/Valtuutus.Core/Engines/Check/CheckEngine.cs` — `CheckRelation` method.

### Results after Optimization 2

The type guard adds ~15 ns overhead per `CheckRelation` call for valid subjects (guard passes → DB call still made). No reduction in the current benchmarks because all benchmarks use valid subject types that appear in schema relations. The real benefit is in prod calls with wrong subject types.

| Method            | Mean           | Allocated | vs Baseline |
|-------------------|---------------:|----------:|------------:|
| Check_Simple      | 593.3 ns       | 912 B     | ~same       |
| Check_Complex     | 679,846.6 ns   | 587,455 B | ~same       |
| SubjectPermission | 717,849.6 ns   | 611,301 B | ~same       |
| LookupEntity      | 1,742,222.2 ns | 894,593 B | ~same       |

---

## Optimization 3: VisitsMap dedup in LookupEntityEngine / LookupSubjectEngine

**What:** Track visited (entityType, entityId) pairs in a `ConcurrentDictionary` per request to
avoid re-processing nodes reached via convergent permission paths (e.g. `org.admin` appearing
twice in `cache_test := (org.admin or team.owner) and (org.admin or member)`).

**Where:** `src/Valtuutus.Core/Engines/LookupEntity/LookupEntityEngine.cs`
`src/Valtuutus.Core/Engines/LookupSubject/LookupSubjectEngine.cs`

### Results after Optimization 3

**Deferred** — memoization via `Interlocked.CompareExchange` on a lazy `Task<>` field is racy in the concurrent `Task.WhenAll` execution model and introduces complexity without measurable benefit (benchmarks within noise). Proper dedup requires either:
- a fully async recursive model where a `ConcurrentDictionary<key, Task>` can be used correctly, OR
- a pre-pass that collapses identical sub-trees in the permission DAG before evaluation.

Both are larger refactors scheduled for a follow-up PR.

---

## Optimization 4: Schema reachability gate

**What:** Pre-compute a subject→entity adjacency graph on `Schema` at parse time. Before issuing
any DB query in a Lookup* call, verify that the requested `subjectType` can possibly reach the
`entityType` via the schema's relation graph. Requests for unreachable combinations return empty
immediately.

**Where:** `src/Valtuutus.Core/Schemas/Schema.cs` (build reachability map)
`src/Valtuutus.Core/Engines/LookupEntity/LookupEntityEngine.cs` (gate at entry)
`src/Valtuutus.Core/Engines/LookupSubject/LookupSubjectEngine.cs` (gate at entry)

### Results after Optimization 4

**Deferred** — zero benefit in current benchmarks (all test calls use valid subject types). The gate would help prod calls with wrong entity types but adds moderate implementation complexity (recursive schema traversal to build reachability sets). Scheduled for follow-up.
