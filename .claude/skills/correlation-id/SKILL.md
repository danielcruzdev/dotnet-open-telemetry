---
name: correlation-id
description: The end-to-end correlation contract for this solution. Use when writing or reviewing anything that touches X-Correlation-Id, OpenTelemetry Baggage, trace propagation between BFF/Core/Proxy, log enrichment with CorrelationId, span processors, or DelegatingHandlers. Also use when a log or span is missing its correlation id, when a trace breaks across a service hop, or when adding a new outbound HTTP call.
---

# Correlation Contract

This is the core of the project. Every service (BFF, Core, Proxy) implements the **same six steps**. If any step is missing in any service, correlation breaks silently — nothing throws, the id just disappears from that hop onward.

Two ids travel together and they are **not** the same thing:

| Id | Origin | Purpose |
|---|---|---|
| `trace_id` (W3C `traceparent`) | Created by OTel instrumentation | Ties spans into one distributed trace. Resets when a trace ends. |
| `correlation.id` (`X-Correlation-Id`) | Created by the BFF | Business id. Survives independently of the trace lifecycle, appears in logs, and is what a human pastes into the dashboard search box. |

Never replace one with the other. Both are required.

## The six steps

All six live in `Shared.Observability` and are wired once per service via a single extension method. A service must never hand-roll its own version.

```csharp
builder.AddCorrelation();   // a unica linha, em cada Program.cs
```

`AddCorrelation` registers the middleware through an `IStartupFilter` (guaranteeing it runs first, which a manual `app.Use...` does not) and attaches `CorrelationIdHandler` through `ConfigureHttpClientDefaults`, so it covers every `HttpClient` — including ones added later. There is nothing per-service to configure: correlation only works if all three services behave identically.

### 1. Inbound — read or generate

Middleware, registered **first** in the pipeline (before routing, before any logging middleware):

```csharp
const string HeaderName = "X-Correlation-Id";

var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
var correlationId = IsValid(incoming) ? incoming! : Guid.NewGuid().ToString("N");
```

`IsValid` = non-empty, length <= 64, and only `[A-Za-z0-9._-]`. Reject anything else and generate a fresh one — an unvalidated header goes straight into logs and span attributes, which is a log-injection vector.

In practice the BFF generates and Core/Proxy receive, but all three can generate. Never assume the header is present.

### 2. Store in Baggage

```csharp
Baggage.SetBaggage("correlation.id", correlationId);
```

`OpenTelemetry.Baggage` is `AsyncLocal`-backed, so it must be set inside the request's async flow — setting it in a singleton constructor or a background thread will not reach the handler.

The OTel SDK's default propagator is a composite of `TraceContextPropagator` + `BaggagePropagator`, so this value is injected into outbound requests as the `baggage` header automatically. Do **not** override `Propagators.DefaultTextMapPropagator` without also keeping `BaggagePropagator` in the composite — that single line silently kills cross-service correlation.

### 3. Tag every span, not just the root

```csharp
internal sealed class CorrelationIdSpanProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity activity)
    {
        var id = Baggage.GetBaggage("correlation.id");
        if (!string.IsNullOrEmpty(id))
            activity.SetTag("correlation.id", id);
    }
}
```

Registered via `.AddProcessor<CorrelationIdSpanProcessor>()` on the tracer provider.

Tagging only the incoming-request span is the common mistake: the DB span, the outbound HTTP span, and any custom span would then be unsearchable by correlation id.

**The processor alone does not cover the server span.** ASP.NET Core instrumentation starts the root span *before* any middleware runs, so `OnStart` fires while the Baggage is still empty. The middleware must also tag it directly:

```csharp
Activity.Current?.SetTag(CorrelationId.TagName, correlationId);
```

Verified: removing that line leaves every child span tagged and the server span bare.

### 4. Enrich every log

Middleware opens a scope that stays open for the whole request:

```csharp
using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
{
    await next(context);
}
```

And the logging provider must opt into scopes:

```csharp
builder.Logging.AddOpenTelemetry(o => o.IncludeScopes = true);
```

`IncludeScopes = false` (the default) means the scope is created and then dropped — the code looks correct and produces nothing. `TraceId` and `SpanId` are attached to the log record by OTel automatically; do not add them to the scope manually.

**One known gap:** `Request starting` and `Request finished`, logged by `Microsoft.AspNetCore.Hosting.Diagnostics`, are emitted outside the middleware pipeline entirely. No scope can reach them, so they carry no `CorrelationId`. This is inherent, not a defect — the contract covers application logs, and `Shared.Observability.Tests` pins the gap so it is never misread as a regression.

### 5. Echo on the response

Set the header before the response starts, using `OnStarting`:

```csharp
context.Response.OnStarting(() =>
{
    context.Response.Headers[HeaderName] = correlationId;
    return Task.CompletedTask;
});
```

Assigning to `Response.Headers` after `await next(context)` throws once the response has begun. `OnStarting` is the only reliable placement.

### 6. Outbound — explicit header

```csharp
internal sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var id = Baggage.GetBaggage("correlation.id");
        if (!string.IsNullOrEmpty(id))
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", id);

        return base.SendAsync(request, ct);
    }
}
```

Attached to every typed client — see the `service-to-service` skill.

This is deliberately redundant with the `baggage` header: the explicit `X-Correlation-Id` on each hop is a hard requirement of this project, because it makes the id visible in any HTTP trace, proxy log, or Fiddler capture without decoding baggage.

## Naming

Fixed across the whole solution. Do not introduce variants.

| Where | Name |
|---|---|
| HTTP header | `X-Correlation-Id` |
| Baggage key | `correlation.id` |
| Span attribute | `correlation.id` |
| Log scope key / property | `CorrelationId` |

Span attributes follow OTel dotted-snake convention; log properties follow .NET PascalCase. The mismatch is intentional and correct.

## Verifying it works

A change to correlation is only done when this holds:

1. `POST` to the BFF without an `X-Correlation-Id` header → response carries one.
2. `POST` again **with** `X-Correlation-Id: teste-123` → the same value comes back, and is present on Core and Proxy spans.
3. In the Aspire Dashboard, filtering traces by `correlation.id` returns spans from all three services.
4. Every log line of the request, in all three services, has `CorrelationId` set.

Automate 1–4 rather than clicking through — see the `telemetry-testing` skill.

## Failure modes worth memorising

| Symptom | Cause |
|---|---|
| Id present in BFF logs, absent in Core | Middleware not registered in Core, or registered after routing |
| Id on the request span only | `CorrelationIdSpanProcessor` not registered |
| Scope created, logs empty | `IncludeScopes` not enabled |
| `X-Correlation-Id` missing on an outbound call while `traceparent` is fine | `HttpClient` created via `new HttpClient()` — see below |
| Baggage empty in the handler | `Baggage.SetBaggage` called outside the request's async flow |
| Everything works locally, breaks behind a gateway | Gateway stripping unknown headers — allowlist `traceparent`, `baggage`, `X-Correlation-Id` |

### What `new HttpClient()` actually breaks

A common claim is that bypassing `IHttpClientFactory` breaks the distributed trace. **On .NET 5+ it does not.** `SocketsHttpHandler` carries `DiagnosticsHandler`, so a raw `HttpClient` still starts a client span and still injects `traceparent` and `baggage`.

Verified by mutation: swapping the factory client for `new HttpClient()` leaves `Chamada_de_saida_leva_traceparent_e_baggage` green and fails only `Chamada_de_saida_leva_o_header_de_correlacao`.

What it really breaks:

- **`X-Correlation-Id`** — the `CorrelationIdHandler` is a `DelegatingHandler` registered through the factory, so it is simply not in the chain.
- **Service discovery** — `https+http://pagamentos-core` never resolves.
- **Resilience** — no retry, no timeout, no circuit breaker.

The rule stands (always use the factory), but diagnose accurately: a trace that still looks connected while the correlation id vanishes at one hop points here, not at the propagators.
