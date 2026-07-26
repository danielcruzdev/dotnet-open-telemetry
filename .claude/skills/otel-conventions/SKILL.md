---
name: otel-conventions
description: OpenTelemetry instrumentation conventions for this .NET 10 solution — ActivitySource naming, span naming, required attributes, semantic conventions, exception recording, metrics, and what not to instrument. Use when adding spans or metrics, configuring the tracer/meter provider, setting resource attributes, wiring OTLP export, or reviewing whether new code is properly instrumented.
---

# OpenTelemetry Conventions

Automatic instrumentation (ASP.NET Core + HttpClient) already produces the server span and the client span for every hop. Your job is to add spans only where they buy something the automatic ones cannot show, and to make sure failures are visible.

## Configuration

All three services call one extension method from `ServiceDefaults`. No service configures OTel by hand.

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: builder.Environment.ApplicationName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString(),
        serviceInstanceId: Environment.MachineName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.RecordException = true)
        .AddHttpClientInstrumentation(o => o.RecordException = true)
        .AddSource(Telemetry.ActivitySourceName)
        .AddProcessor<CorrelationIdSpanProcessor>()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(Telemetry.MeterName)
        .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter();
});
```

The Aspire AppHost injects `OTEL_EXPORTER_OTLP_ENDPOINT` — never hardcode the endpoint.

`serviceName` is what separates the three services in the dashboard. Getting it wrong makes every span look like it came from one app.

## ActivitySource and Meter

One of each per service, defined once as static, named after the assembly:

```csharp
public static class Telemetry
{
    public const string ActivitySourceName = "Pagamentos.Core";
    public const string MeterName = "Pagamentos.Core";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
}
```

`ActivitySource` and `Meter` are thread-safe and meant to be long-lived — never create one per request. And every source name must be passed to `.AddSource(...)`: an unregistered source produces `Activity.Current == null` and your spans vanish with no error.

## When to create a span

**Create one** for: a business operation worth timing on its own (score calculation, PIX key validation), a loop over external calls, or work that happens off the request thread.

**Do not create one** for: wrapping a single outbound HTTP call (HttpClient instrumentation already made that span — you would only add a duplicate parent), trivial pure functions, or property mapping. Every span costs storage and makes the trace harder to read.

```csharp
using var activity = Telemetry.Source.StartActivity("ValidarChavePix");
activity?.SetTag("pix.chave.tipo", tipo);
```

Note the `?.` — when nobody is listening, `StartActivity` returns `null` by design.

## Span naming

Low-cardinality, `VerbNoun`, no ids: `ValidarChavePix`, `CalcularTarifa`, `ConsultarFornecedor`. Never `Pagamento 4f2a-...` — a unique name per request destroys aggregation in the dashboard.

## Attributes

| Attribute | Where | Notes |
|---|---|---|
| `correlation.id` | every span | Added automatically by the processor — see the `correlation-id` skill |
| `pagamento.id` | spans in the payment flow | The business id, once known |
| `pagamento.status` | the span that resolves it | `aprovado`, `recusado`, `pendente` |
| `fornecedor.nome` | Proxy spans | Which simulated partner |
| `erro.motivo` | on failure | Business reason, e.g. `saldo_insuficiente` |

**Never** put a PIX key, CPF/CNPJ, name, email, phone, token, or full payment amount tied to an identified person into an attribute. Attributes are stored unredacted and are readable by anyone with dashboard access. Use `pix.chave.tipo` (`cpf`, `email`, `aleatoria`), not the key itself.

Prefer existing HTTP/RPC semantic conventions over inventing names; invent only for domain concepts, and keep the `dominio.propriedade` dotted-snake shape.

## Recording failures

An exception that is caught and converted into a 4xx/5xx will not mark the span as failed on its own. Both lines are required:

```csharp
catch (FornecedorIndisponivelException ex)
{
    activity?.AddException(ex);
    activity?.SetStatus(ActivityStatusCode.Error, "fornecedor_indisponivel");
    return TypedResults.Problem(...);
}
```

Expected business rejections (insufficient funds, invalid key) are **not** errors. Leave the status `Unset` and record the outcome as an attribute — otherwise the error rate becomes noise and real outages hide in it.

## Metrics

Three per service, no more, defined next to the `Meter`:

- `pagamentos.solicitados` — `Counter<long>`, tagged with `status`
- `pagamentos.duracao` — `Histogram<double>` in seconds, the end-to-end business duration
- `fornecedor.chamadas` — `Counter<long>` (Proxy only), tagged with `resultado`

Request rate, latency and error rate per endpoint already come from `AddAspNetCoreInstrumentation`. Do not re-implement them.

Metric tags must be low-cardinality. A `correlation.id` or `pagamento.id` tag on a metric creates one time series per request and will bring down the backend — that is the single most damaging mistake in this file.
