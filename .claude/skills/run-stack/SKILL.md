---
name: run-stack
description: How to run this solution locally via the .NET Aspire AppHost, open the Aspire Dashboard, fire a test payment through BFF/Core/Proxy, and find the resulting trace. Use when asked to run, start, or demo the app, when verifying a change end-to-end, when investigating a request in the dashboard, or when the stack fails to start.
---

# Running the Stack

Everything starts from the AppHost. Never run the three services individually — you lose service discovery, the OTLP endpoint injection, and therefore the whole point of the project.

```bash
dotnet run --project src/Pagamentos.AppHost
```

The console prints the dashboard URL with a one-time login token:

```
Login to the dashboard at http://localhost:15888/login?t=<token>
```

That token changes on every start. Always take the URL from the current run's output — a saved link will not authenticate.

Requires .NET SDK 10 (`dotnet --list-sdks`) and a **trusted** HTTPS development certificate.

## The dev certificate is a hard prerequisite

The AppHost's default `https` launch profile exposes the OTLP endpoint over HTTPS. If the ASP.NET Core dev cert is untrusted, every service starts and answers `/health` normally, but **no telemetry ever reaches the dashboard** — the exporter fails the TLS handshake in the background and the dashboard simply looks empty. Nothing in the service logs says why.

Check and fix before anything else:

```bash
dotnet dev-certs https --check --trust
```

If it reports certificates found but none trusted, run `dotnet dev-certs https --trust` and accept the Windows dialog. This is interactive — it cannot be done from an automated session.

To confirm the failure when you suspect it, enable OTel self-diagnostics: drop an `OTEL_DIAGNOSTICS.json` next to the service binary with `{"LogDirectory": ".", "FileSize": 1024, "LogLevel": "Warning"}`, run, and grep the generated log. The signature is:

```
AuthenticationException: The remote certificate is invalid because of
errors in the certificate chain: UntrustedRoot
→ https://localhost:<porta>/...TraceService/Export
```

Delete the file afterwards — it fills a megabyte with unrelated MsQuic meter noise.

The `http` launch profile is not an escape hatch: the AppHost refuses to start on a non-HTTPS `applicationUrl` unless `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` is also set. Trusting the certificate is the correct fix.

## Firing a test payment

The BFF is the only entry point. Its port is assigned by the AppHost — read it from the dashboard's Resources page, do not guess.

```bash
curl -i -X POST http://localhost:<porta-bff>/pagamentos \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: teste-123" \
  -d '{"chavePix":"teste@exemplo.com","valor":150.00,"descricao":"teste"}'
```

Sending an explicit `X-Correlation-Id` makes the trace trivial to find. Omit it to verify that the BFF generates one — the response header must then carry a fresh id.

Check the response headers first: `X-Correlation-Id` must come back on every call. If it does not, stop and fix that before looking at the dashboard.

## Finding the trace

In the dashboard:

1. **Traces** → filter by `correlation.id = teste-123`, or click the most recent trace.
2. A healthy trace is one tree spanning three services: BFF server span → BFF client span → Core server span → Core client span → Proxy server span → Proxy's simulated partner call.
3. Open any span and confirm the `correlation.id` attribute is present — on **every** span, not only the root.
4. **Structured logs** → filter by `CorrelationId`. Logs from all three services appear in one timeline.
5. From a span, jump straight to that request's logs via the trace link.

Symptoms and their meaning:

| What you see | Cause |
|---|---|
| Two separate traces instead of one | Propagation broken at that hop — check the typed client registration |
| Spans present, `correlation.id` missing | Span processor not registered in that service |
| Logs present, `CorrelationId` missing | `IncludeScopes` not enabled |
| Only one service in the trace | Downstream call not going through an instrumented `HttpClient` |

The `correlation-id` skill has the full failure table.

## Fault scenarios

The Proxy simulates the external partner and can be pushed into failure to exercise the whole point of the tracing — seeing *where* it broke:

- Insufficient balance → business rejection, `Warning` logs, span status stays `Unset`
- Partner timeout → 504 upstream, `Error`, span status `Error`
- Partner unavailable → 502 upstream, retry attempts visible as sibling client spans
- Invalid PIX key → rejected at the Core, the Proxy is never called

For each, the trace must show which service produced the failure and the `erro.motivo` attribute must name it.

## Shutting down

`Ctrl+C` in the AppHost terminal stops every resource. If a port stays bound afterwards, look for an orphaned `dotnet` process before assuming a code problem.

## Before claiming a change works

Run the stack, fire a payment, and confirm in the dashboard: one trace, three services, `correlation.id` on every span, `CorrelationId` on every log, `X-Correlation-Id` on the response. A passing test suite is necessary but does not replace this check — it is the actual acceptance criterion of the project.
