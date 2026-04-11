# Telemetry

Valtuutus uses [OpenTelemetry](https://opentelemetry.io/) `ActivitySource` to emit distributed traces. Two sources are available, with different granularity:

| Source name | Purpose |
|---|---|
| `"Valtuutus"` | Top-level spans for each public engine call — recommended for production |
| `"Valtuutus.Internal"` | Granular internal spans for every internal step — useful for debugging |

## Setup

Subscribe to the sources you want when configuring OpenTelemetry:

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Valtuutus")           // top-level engine calls
        // .AddSource("Valtuutus.Internal") // uncomment for detailed internal spans
        .AddJaegerExporter()              // or any other exporter
    );
```

For day-to-day production monitoring, subscribe only to `"Valtuutus"`. The internal source emits a span for every node traversed in the schema graph, which can be extremely noisy on complex schemas.

## Emitted spans

### `Check`
- **Source:** `Valtuutus`
- **Tags:** `CheckRequest` — the full request object
- **Events:** `CheckFinished` with tag `CheckResult` (bool) — the final allow/deny decision

### `SubjectPermission`
- **Source:** `Valtuutus`
- **Tags:** `SubjectPermissionRequest` — the full request object
- **Events:** `SubjectPermissionFinished` — one tag per permission name, value is the bool result (e.g. `view = true`, `edit = false`)

### `LookupSubject`
- **Source:** `Valtuutus`
- **Tags:** request attributes
- **Events:** `LookupSubjectResult` — the set of matching subject IDs

### `LookupEntity`
- **Source:** `Valtuutus`
- **Tags:** request attributes
- **Events:** `LookupEntityResult` — the count of matching entity IDs

### Internal spans (`Valtuutus.Internal`)
The internal source emits a child span for every step the engine takes while traversing the schema graph: `CheckPermission`, `CheckAttribute`, `CheckExpression`, `NegateCheck`, `LookupNegate`, `LookupRelationLeaf`, `LookupRelationCore`, and others. These are useful when diagnosing unexpected `false` results or performance issues on specific paths.

## What to monitor in production

The top-level `Check`, `LookupSubject`, and `LookupEntity` spans give you:

- **Latency** per engine call, broken down by entity type and permission
- **The final result** via the `CheckFinished` / `LookupSubjectResult` / `LookupEntityResult` events
- **Fan-out visibility** — because internal spans are children of the top-level span, enabling `Valtuutus.Internal` shows exactly how many DB calls a single engine call triggered and where time was spent
