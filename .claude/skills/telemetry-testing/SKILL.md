---
name: telemetry-testing
description: How to write tests for this solution — unit tests for slice handlers, integration tests with WebApplicationFactory, and telemetry tests that assert traceId/correlationId actually propagate across service hops and reach logs and spans. Use when adding tests, when a correlation regression needs a guard, or when deciding what to assert about spans, log records, or headers.
---

# Testing

Three layers, each answering a different question:

| Layer | Question | Tooling |
|---|---|---|
| Unit | Is the business rule right? | xUnit, plain calls |
| Integration | Does the endpoint behave? | `WebApplicationFactory<Program>` |
| Telemetry | Did correlation survive? | In-memory OTel exporters |

The third layer is the one that protects the point of this project. Correlation breaks silently — no exception, no failing endpoint test — so without these tests a regression ships unnoticed.

## Unit tests

Slice handlers are `private static`, so test the observable behaviour through the integration layer rather than making the handler public just to test it. Reserve unit tests for logic that stands on its own: validators, PIX key parsing, fee calculation.

Mirror the source layout: `tests/Pagamentos.Core.Tests/Features/Pagamentos/CriarPagamento/`.

## Integration tests

```csharp
public sealed class CriarPagamentoTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Deve_devolver_correlation_id_no_response()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/pagamentos", ValidRequest);

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle();
    }
}
```

`Program.cs` needs `public partial class Program;` at the end for `WebApplicationFactory<Program>` to bind. Without it the test project fails to compile with an accessibility error.

Downstream services are replaced in `ConfigureTestServices` with a stub typed client — these tests must not depend on Core or Proxy actually running.

## Telemetry tests

Capture spans and log records in memory:

```csharp
private readonly List<Activity> _spans = [];
private readonly List<LogRecord> _logs = [];

protected override void ConfigureWebHost(IWebHostBuilder builder) =>
    builder.ConfigureTestServices(services =>
    {
        services.AddOpenTelemetry()
            .WithTracing(t => t.AddInMemoryExporter(_spans));

        services.AddLogging(l => l.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.AddInMemoryExporter(_logs);
        }));
    });
```

The exporter batches by default. Force a flush before asserting or the lists will be empty and the test fails for the wrong reason:

```csharp
factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
```

### What to assert

Assertions use plain xUnit `Assert` — this solution does not take a fluent-assertion dependency.

**Every span carries the id — not just the root:**

```csharp
Assert.NotEmpty(spans);
var semTag = spans.Where(s => s.GetTagItem("correlation.id") as string != "teste-123")
                  .Select(s => s.DisplayName).ToList();
Assert.Empty(semTag);   // lista os culpados quando falha
```

Asserting only on the root span is the mistake this test exists to catch: it passes even when the span processor is missing. Keep a *separate* test for the server span specifically — it is the one the processor cannot reach, so only a dedicated assertion catches a missing manual tag.

**An incoming id is honoured, not replaced:**

```csharp
request.Headers.Add("X-Correlation-Id", "teste-123");
// ... response header and every span must be "teste-123"
```

**A missing id is generated and echoed back.**

**An invalid id is rejected and replaced** — send `X-Correlation-Id: "; DROP` and assert the response header is a fresh 32-char hex value, not the input.

**Logs carry the id.** The id lives in a *scope*, not in `LogRecord.Attributes` — read it with `ForEachScope`:

```csharp
string? id = null;
log.ForEachScope((scope, _) =>
{
    foreach (var item in scope)
        if (item.Key == "CorrelationId") id = item.Value as string;
}, default(object));
```

This catches a missing `IncludeScopes = true`, which nothing else detects.

Exclude `Microsoft.AspNetCore.Hosting.Diagnostics` from the assertion — its `Request starting`/`Request finished` records are emitted outside the middleware pipeline and legitimately have no scope. Assert that gap explicitly in its own test so nobody later reads it as a regression.

**The outbound call carries the propagation headers.** Do **not** capture it with `ConfigurePrimaryHttpMessageHandler` and a fake handler. Replacing the primary handler removes .NET's `DiagnosticsHandler` from the chain, and with it both the client span and the injection of `traceparent`/`baggage` — the test then fails for a reason that has nothing to do with your code.

Point the client at a **real** loopback server instead and record what arrives:

```csharp
var eco = WebApplication.CreateSlimBuilder();
eco.WebHost.UseUrls("http://127.0.0.1:0");          // porta dinamica
// endpoint que copia context.Request.Headers
```

Then assert `X-Correlation-Id`, `traceparent` and `baggage` on the received headers. This is the actual hop, and where propagation most often breaks.

**Trace continuity across a hop:** start an `Activity` in the test, call the endpoint, and assert the server span's `TraceId` equals the test activity's `TraceId` and its `ParentSpanId` is the test span. A new `TraceId` means the trace was broken.

## Faults

Assert the failure shape too: the Proxy fault injection returns a partner outage, and the test verifies the Core span has `ActivityStatusCode.Error` with `erro.motivo = fornecedor_indisponivel`, while an insufficient-balance rejection leaves the status `Unset`. That distinction is a real rule from the `otel-conventions` skill and deserves a guard.

## Checklist

1. Test folder mirrors the source slice folder.
2. `public partial class Program;` present in the service under test.
3. `ForceFlush()` on `TracerProvider`/`LoggerProvider`/`MeterProvider` before asserting.
4. Span assertions cover *every* span, never just the first.
5. Incoming, missing and invalid correlation id all covered.
6. Outbound headers asserted against a real loopback server, not a fake primary handler.
7. Auxiliary test servers filtered out of the assertions (see below).
8. No test depends on another service being up.

## Auxiliary hosts leak into your spans

`AddAspNetCoreInstrumentation` subscribes to the ASP.NET Core `DiagnosticSource` for the whole **process**, not for one host. A loopback echo server started inside the test therefore produces server spans in your exporter — and it has no `AddCorrelation`, so those spans have no `correlation.id` and fail an "every span" assertion.

Filter them out at the source:

```csharp
.AddAspNetCoreInstrumentation(o => o.Filter =
    context => !context.Request.Path.StartsWithSegments("/recebe"))
```

The same cause bites across **test classes**: xUnit runs classes in parallel, so a second `WebApplicationFactory` alive at the same time drops its server spans into your exporter and `Single(s => s.Kind == Server)` starts throwing "more than one matching element". Any test project that asserts on spans needs:

```csharp
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

## A span-status assertion can pass for the wrong reason

ASP.NET Core instrumentation already sets `ActivityStatusCode.Error` on **any** 5xx response. So this assertion holds even if your own `SetStatus` is deleted:

```csharp
Assert.Equal(ActivityStatusCode.Error, span.Status);   // nao prova nada
```

Assert the description too — that value comes only from your `SetStatus(Error, motivo)`:

```csharp
Assert.Equal(motivo, span.StatusDescription);
```

The general rule: before writing a telemetry assertion, name the line of production code whose removal would break it. If you cannot, the framework is doing the work and the test is decorative.

## Prove the test can fail

A telemetry test that has never been seen failing proves nothing — the whole failure mode here is silence. After it goes green, remove the thing it protects and confirm it goes red:

| Remove | Must fail |
|---|---|
| `AddProcessor(new CorrelationIdSpanProcessor())` | every-span test |
| `Activity.Current?.SetTag(...)` in the middleware | server-span test |
| the `BeginScope` contents | log test |
| `CorrelationIdHandler` from the client defaults | outbound-header test |

Then restore and confirm green again.
