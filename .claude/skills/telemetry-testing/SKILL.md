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

**Every span carries the id — not just the root:**

```csharp
_spans.Should().NotBeEmpty();
_spans.Should().OnlyContain(s =>
    s.GetTagItem("correlation.id") as string == "teste-123");
```

Asserting only on the root span is the mistake this test exists to catch: it passes even when the span processor is missing.

**An incoming id is honoured, not replaced:**

```csharp
request.Headers.Add("X-Correlation-Id", "teste-123");
// ... response header and every span must be "teste-123"
```

**A missing id is generated and echoed back.**

**An invalid id is rejected and replaced** — send `X-Correlation-Id: "; DROP` and assert the response header is a fresh 32-char hex value, not the input.

**Logs carry the id:**

```csharp
_logs.Should().OnlyContain(r =>
    r.Attributes!.Any(a => a.Key == "CorrelationId"));
```

This one catches a missing `IncludeScopes = true`, which nothing else detects.

**The outbound call carries the header** — assert on the stubbed handler's captured `HttpRequestMessage` that `X-Correlation-Id`, `traceparent` and `baggage` are all present. This is the actual hop, and it is where propagation most often breaks.

**Trace continuity across a hop:** start an `Activity` in the test, call the endpoint, and assert the server span's `TraceId` equals the test activity's `TraceId` and its `ParentSpanId` is the test span. A new `TraceId` means the trace was broken.

## Faults

Assert the failure shape too: the Proxy fault injection returns a partner outage, and the test verifies the Core span has `ActivityStatusCode.Error` with `erro.motivo = fornecedor_indisponivel`, while an insufficient-balance rejection leaves the status `Unset`. That distinction is a real rule from the `otel-conventions` skill and deserves a guard.

## Checklist

1. Test folder mirrors the source slice folder.
2. `public partial class Program;` present in the service under test.
3. `ForceFlush()` before asserting on spans or logs.
4. Span assertions use `OnlyContain`, never just the first span.
5. Incoming, missing and invalid correlation id all covered.
6. Outbound headers asserted on a captured request.
7. No test depends on another service being up.
