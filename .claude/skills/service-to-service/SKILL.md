---
name: service-to-service
description: How BFF calls Core and Core calls Proxy in this solution — typed HttpClients, the correlation DelegatingHandler, Aspire service discovery, resilience/retry, and how to map a downstream failure into an upstream response without losing the trace. Use when adding an outbound HTTP call, registering an HttpClient, configuring retries or timeouts, or deciding what status code to return when a downstream service fails.
---

# Service-to-Service Calls

Chain: **BFF → Core → Proxy → simulated partner**. Every link uses the same pattern. A call made any other way — `new HttpClient()`, a static `HttpClient` field, raw `HttpRequestMessage` sent through an unregistered client — has no instrumentation, so the trace ends there and the correlation id never arrives.

## Typed client

Interface plus implementation in `Infrastructure/`, one per downstream service:

```csharp
public interface IPagamentosCoreClient
{
    Task<CriarPagamentoResponse> CriarAsync(CriarPagamentoRequest request, CancellationToken ct);
}

internal sealed class PagamentosCoreClient(HttpClient http) : IPagamentosCoreClient
{
    public async Task<CriarPagamentoResponse> CriarAsync(
        CriarPagamentoRequest request, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/pagamentos", request, ct);

        if (!response.IsSuccessStatusCode)
            throw await CoreIndisponivelException.FromResponseAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<CriarPagamentoResponse>(ct)
               ?? throw new CoreIndisponivelException("Resposta vazia do Core.");
    }
}
```

The client's job is transport and deserialization only. Business decisions belong in the slice.

## Registration

```csharp
builder.Services.AddTransient<CorrelationIdHandler>();

builder.Services.AddHttpClient<IPagamentosCoreClient, PagamentosCoreClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://pagamentos-core");
        client.Timeout = TimeSpan.FromSeconds(10);
    })
    .AddHttpMessageHandler<CorrelationIdHandler>()
    .AddStandardResilienceHandler();
```

Three things matter here:

- **`https+http://pagamentos-core`** is Aspire service discovery. The name must match the resource name in the AppHost exactly. Never hardcode a port or `localhost` — that breaks the moment the AppHost assigns a different port.
- **`AddHttpMessageHandler<CorrelationIdHandler>`** is what puts `X-Correlation-Id` on the wire. Omit it on one client and correlation dies at that hop.
- **`AddStandardResilienceHandler`** (`Microsoft.Extensions.Http.Resilience`) gives retry, circuit breaker and timeout. Do not hand-roll a Polly pipeline.

`CorrelationIdHandler` is registered `Transient` — a `DelegatingHandler` instance cannot be shared between clients, and registering it as a singleton throws at resolution time.

## Retries and the trace

Each retry attempt produces its own client span under the same trace, which is exactly what you want when investigating: three spans to the Proxy makes the retry visible instead of appearing as one slow call.

Two constraints:

- The client `Timeout` must be larger than the total resilience budget, otherwise the outer timeout cancels the pipeline mid-retry and you get a confusing `TaskCanceledException` instead of the real downstream error.
- Only idempotent operations should retry. `POST /pagamentos` creating a payment is not idempotent — either restrict retries to timeouts and connection failures, or carry an idempotency key. Retrying a 500 on a payment that already succeeded duplicates it.

## Cancellation

Pass `CancellationToken` down every level, from the endpoint parameter into the client into `PostAsJsonAsync`. Dropping it means an abandoned client request keeps the whole chain busy and produces spans for work nobody is waiting on.

## Mapping downstream failures upward

Never let a downstream `HttpRequestException` bubble out raw — the caller gets a 500 with no reason and the span carries no business meaning.

| Downstream result | What the caller returns | Log level |
|---|---|---|
| Business rejection (422 with a reason) | Same rejection, translated to the caller's contract | `Warning` |
| 5xx / connection failure after retries | `502 Bad Gateway`, `erro.motivo = fornecedor_indisponivel` | `Error` |
| Timeout | `504 Gateway Timeout`, `erro.motivo = fornecedor_timeout` | `Error` |
| 4xx caused by our own request | `500` — this is our bug, not the partner's | `Error` |

Preserve the reason as it travels up. If the Proxy says `saldo_insuficiente`, the BFF response must still say `saldo_insuficiente` — a request that turns into a generic "erro interno" two hops later is exactly the problem this project exists to fix.

Set `activity?.SetStatus(ActivityStatusCode.Error, motivo)` on infrastructure failures, and leave the status unset for business rejections — see the `otel-conventions` skill.

## Checklist

1. Typed client with an interface, in `Infrastructure/`.
2. Registered via `AddHttpClient<TInterface, TImpl>` — never constructed manually.
3. `BaseAddress` uses the Aspire service name.
4. `.AddHttpMessageHandler<CorrelationIdHandler>()` present.
5. `.AddStandardResilienceHandler()` present, timeout above the retry budget.
6. `CancellationToken` threaded through.
7. Failure mapped to a status code and a `erro.motivo`, reason preserved.
8. Trace verified end-to-end in the dashboard after the change.
