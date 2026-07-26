---
name: vertical-slice
description: Vertical Slice Architecture conventions for the .NET 10 Minimal API services in this solution. Use when adding a new endpoint or feature, deciding where a file belongs, naming handlers/requests/responses, registering routes, or reviewing whether code was placed in the right slice. Also use when tempted to create a Services/, Repositories/, or Models/ folder.
---

# Vertical Slice Conventions

One feature = one folder = one file (plus its test). Everything a request needs lives together: route, contract, validation, handler, and the mapping to whatever it calls next. You should be able to delete a feature by deleting its folder.

## Layout

```
src/Pagamentos.Core/
├── Features/
│   └── Pagamentos/
│       ├── CriarPagamento/
│       │   ├── CriarPagamentoEndpoint.cs     # route + contract + handler
│       │   └── CriarPagamentoValidator.cs    # only if validation is non-trivial
│       └── ConsultarPagamento/
│           └── ConsultarPagamentoEndpoint.cs
├── Infrastructure/                            # typed clients, persistence — cross-feature only
└── Program.cs
```

Folder names are the domain language of the project — Portuguese, matching the PRD (`Pagamentos`, `CriarPagamento`). Class members, C# keywords and technical types stay English. Do not translate `Handler`, `Request`, `Response`.

## The single-file slice

```csharp
namespace Pagamentos.Core.Features.Pagamentos.CriarPagamento;

public sealed record CriarPagamentoRequest(string ChavePix, decimal Valor, string Descricao);

public sealed record CriarPagamentoResponse(Guid PagamentoId, string Status);

public static class CriarPagamentoEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/pagamentos", Handle)
           .WithName("CriarPagamento")
           .WithTags("Pagamentos");

    private static async Task<Results<Ok<CriarPagamentoResponse>, ProblemHttpResult>> Handle(
        CriarPagamentoRequest request,
        IFornecedorClient fornecedor,
        ILogger<CriarPagamentoRequest> logger,
        CancellationToken ct)
    {
        // ...
    }
}
```

Rules:

- `Handle` is `private static`. Dependencies arrive as parameters — Minimal API resolves them from DI. No constructor injection, no handler class, no MediatR.
- Request and response records are `sealed` and live in the same file. They are **not** shared between slices, even when identical today.
- Return `Results<T1, T2>` (typed results), never `IResult` — the typed union documents the contract and drives OpenAPI.
- `CancellationToken` is always the last parameter and is always passed downstream.

## Registration

Each slice exposes `Map(IEndpointRouteBuilder)`. `Program.cs` calls them explicitly:

```csharp
CriarPagamentoEndpoint.Map(app);
ConsultarPagamentoEndpoint.Map(app);
```

Explicit over reflection-based discovery: it is greppable, it fails at compile time when a slice is renamed, and the registration order is visible.

## What may be shared

Shared code is the exception and needs a reason.

**Allowed in `Infrastructure/`:** typed HTTP clients (one per downstream service), persistence, and anything the platform owns.

**Allowed in `Shared.Observability`:** the correlation contract, `ActivitySource` definitions, telemetry extension methods.

**Not allowed:** a `Services/` layer, a shared `PagamentoDto`, a base handler, a generic `Result<T>` wrapper written for this project, or a repository interface with one implementation. Duplication between two slices is cheaper than a coupling that forces both to change together.

If two slices genuinely need the same logic, first ask whether they are actually the same feature. Only promote to shared code on the third occurrence.

## Adding a slice — checklist

1. Folder under `Features/<Contexto>/<Acao>/`.
2. One endpoint file: request, response, `Map`, `Handle`.
3. Register in `Program.cs`.
4. Instrument per the `otel-conventions` skill — a custom span only if the slice does real work beyond the automatic HTTP span.
5. Log per the `structured-logging` skill.
6. Test alongside: `tests/Pagamentos.Core.Tests/Features/Pagamentos/CriarPagamento/`.
7. Downstream call? Use the typed client from the `service-to-service` skill — never `new HttpClient()`.
