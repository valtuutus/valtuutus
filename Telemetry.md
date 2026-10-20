# Telemetry

Valtuutus emits [OpenTelemetry](https://opentelemetry.io/) traces through an `ActivitySource` named `"Valtuutus"`. Each engine call gets one span, and every database round trip it makes gets a child span. When nothing listens to the source, no span is created and nothing is allocated for it.

## Setup

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Valtuutus")
        .AddOtlpExporter()                // or any other exporter
    );
```

`DefaultActivitySource.SourceName` holds the same name.

## What a trace looks like

```
valtuutus.cache                  (only with Valtuutus.Data.Caching)
└── valtuutus.check              entity, permission, subject, snap token, result
    ├── valtuutus.round_trip     queries = [HasDirectRelation, GetIndirectRelations], resolves_snap_token
    └── valtuutus.round_trip     queries = [HasDirectRelation]
```

The engines evaluate a request in waves: the queries a wave needs go to the database together. On Postgres and SqlServer they share one `DbBatch` round trip whenever they can be batched; the in-memory provider and the remaining queries run one per round trip. So the number of `valtuutus.round_trip` children, and their `valtuutus.queries`, show how the engine spent the request. Lookups advance in steps instead of waves, and each round trip records the step that ran it.

A check answers as soon as the result is known. Round trips for branches that no longer matter can still be running at that point. When the database responds to one of them after the engine has answered, its span carries `valtuutus.straggler = true`. The round trip that delivered the answer can still end slightly after the engine span, because the engine answers from its first results while the rest are being read; it is not marked.

## Spans

### Engine calls

| Span | Call |
|---|---|
| `valtuutus.check` | `ICheckEngine.Check` |
| `valtuutus.subject_permission` | `ICheckEngine.SubjectPermission` |
| `valtuutus.explain` | `ICheckEngine.Explain` |
| `valtuutus.lookup_entity` | `ILookupEntityEngine.LookupEntity` |
| `valtuutus.lookup_subject` | `ILookupSubjectEngine.Lookup` |

| Attribute | Type | Present on |
|---|---|---|
| `valtuutus.entity_type` | string | all |
| `valtuutus.entity_id` | string | check, subject_permission, explain, lookup_subject |
| `valtuutus.permission` | string | check, explain, lookup_entity, lookup_subject |
| `valtuutus.subject_type` | string | all (when the request has one) |
| `valtuutus.subject_id` | string | check, subject_permission, explain, lookup_entity |
| `valtuutus.subject_relation` | string | check, explain (when the request has one) |
| `valtuutus.snap_token.source` | string | all: `request` when the caller passed a snap token, `latest` when the engine resolved the latest one |
| `valtuutus.snap_token` | string | all: the snap token the call read at |
| `valtuutus.result` | bool | check, explain |
| `valtuutus.result_count` | int | subject_permission (permissions allowed), lookup_entity (entities found, before paging), lookup_subject (subjects found) |
| `valtuutus.round_trips` | int | all: round trips started before the answer |
| `valtuutus.steps` | int | lookup_entity, lookup_subject |

A call that throws sets the span status to `Error` and adds `error.type` with the exception type.

### `valtuutus.round_trip`

One database round trip. Span kind `Client`.

| Attribute | Type | Meaning |
|---|---|---|
| `valtuutus.queries` | string[] | The queries the round trip ran, in order: one entry for a single query, one per command for a batch |
| `valtuutus.query_count` | int | Number of entries in `valtuutus.queries` |
| `valtuutus.resolves_snap_token` | bool | The batch also resolved the request's latest snap token. Present only when true |
| `valtuutus.step` | int | Lookups: the step that ran the round trip |
| `valtuutus.straggler` | bool | The engine had already answered when the database responded. Present only when true |
| `db.system.name` | string | `postgresql` or `microsoft.sql_server`; absent for the in-memory provider |

### `valtuutus.write` and `valtuutus.delete`

One write or delete. When Valtuutus owns the transaction, the span covers the commit. When you pass your own transaction, the span ends before you commit.

| Attribute | Type | Meaning |
|---|---|---|
| `valtuutus.snap_token` | string | The snap token of the write |
| `db.system.name` | string | As on round trips |

### `valtuutus.cache`

With `Valtuutus.Data.Caching`, each engine call first opens this span. On a miss, the engine span is its child. On a hit, the span has no children.

| Attribute | Type | Meaning |
|---|---|---|
| `valtuutus.operation` | string | `check`, `subject_permission`, `lookup_entity` or `lookup_subject` |
| `valtuutus.cache.hit` | bool | Whether the cache answered |

## What to look at

- **Slow calls:** compare the engine span's duration with its round trips. Many sequential round trips point to a deep schema path. A single slow one points to the database.
- **Wasted work:** a high share of round trips with `valtuutus.straggler = true` means many checks short-circuit while other branches are still running.
- **Consistency questions:** `valtuutus.snap_token` shows which snapshot each answer was read at.
- **Raw SQL:** Valtuutus doesn't put SQL on its spans. Npgsql and Microsoft.Data.SqlClient have their own OpenTelemetry instrumentation that adds a span per command.
