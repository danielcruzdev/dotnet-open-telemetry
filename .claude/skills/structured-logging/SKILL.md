---
name: structured-logging
description: Structured logging rules for the BFF/Core/Proxy services — message templates, standard fields, log levels per hop, sensitive PIX data that must never be logged, and how logs tie back to traces. Use when adding or reviewing any ILogger call, choosing a log level, or debugging why a log line lacks its CorrelationId.
---

# Structured Logging

Logs in this solution exist for one purpose: given a `CorrelationId`, reconstruct the path of a request across the three services and see where it broke. Anything that does not serve that is noise.

## Templates, never interpolation

```csharp
// correct — ChavePixTipo and Valor become queryable fields
logger.LogInformation(
    "Pagamento solicitado tipo={ChavePixTipo} valor={Valor}", tipo, valor);

// wrong — collapses into an unsearchable string, and defeats log sampling
logger.LogInformation($"Pagamento solicitado tipo={tipo} valor={valor}");
```

Interpolation and `string.Concat` are the failure here, not the wording. Placeholder names are PascalCase and match the property they carry.

## Standard fields

`CorrelationId`, `TraceId` and `SpanId` are attached automatically — by the middleware scope and by the OTel logging provider respectively. **Never** add them by hand to a message template; you would get a second, divergent copy of the same value.

If `CorrelationId` is missing from a log line, the bug is in the middleware or in `IncludeScopes` — see the `correlation-id` skill. Do not paper over it by passing the id explicitly at each call site.

## Levels

| Level | Use | Example |
|---|---|---|
| `Trace`/`Debug` | Local diagnostics only. Off in any deployed environment. | Payload dumps |
| `Information` | Business milestones. One at entry, one at outcome, per service. | `Pagamento aprovado`, `Chamando fornecedor` |
| `Warning` | Handled, expected rejections and degradations. | `saldo_insuficiente`, retry attempt |
| `Error` | Failures that need a human. Always with the exception object. | Partner unavailable after retries |
| `Critical` | The service cannot serve traffic. | Startup failure |

A business rejection is `Warning`, not `Error`. If every insufficient-balance response logs at `Error`, the error dashboard becomes useless and real outages hide inside it.

Always pass the exception as the first argument — `logger.LogError(ex, "...")`. `ex.Message` inside the template loses the stack trace and the inner exception.

## How much to log per hop

Two `Information` lines per service per request: one when the work starts, one with the outcome. Plus a `Warning`/`Error` when something goes wrong.

That is six lines for a healthy end-to-end payment. The automatic ASP.NET Core and HttpClient instrumentation already records the request/response of every hop as spans — re-logging status codes and durations duplicates data you already have in the trace.

Do not log inside loops without a guard, and do not log the same event in both the caller and the callee.

## Never log

PIX key, CPF/CNPJ, account or card number, full name, email, phone, tokens, Authorization headers, or full request/response bodies.

Log the shape instead:

```csharp
logger.LogInformation(
    "Chave PIX validada tipo={ChavePixTipo} valida={Valida}", tipo, valida);
```

This applies to exception messages too. An exception carrying a PIX key in its message ends up in the log store the moment it is caught — sanitise when constructing the exception, not when logging it.

Amounts are fine on their own; an amount plus an identifiable person is not.

## Per-service focus

- **BFF** — records that the request arrived and which correlation id it was given. It is the only place where a new id is normally born, so its entry log is the anchor of the whole investigation.
- **Core** — the business decisions: what was validated, what was accepted or rejected, and why.
- **Proxy** — which simulated partner was called and what came back, including injected latency and faults. This is where most investigations end, so its outcome log must always name the reason.

## Checklist

1. Message template with named placeholders — no interpolation.
2. Level matches the table above; business rejection is `Warning`.
3. Exception passed as the first argument, not formatted into the text.
4. No sensitive field, in the template or in the values.
5. Not duplicating what a span already records.
6. `CorrelationId` present at runtime — verify, do not assume.
