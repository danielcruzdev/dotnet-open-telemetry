using Microsoft.AspNetCore.Http.HttpResults;
using Pagamentos.Proxy.Infrastructure;

namespace Pagamentos.Proxy.Features.Fornecedor.ConsultarPagamento;

public sealed record ConsultarPagamentoResponse(Guid PagamentoId, string Status);

public static class ConsultarPagamentoEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/fornecedor/pagamentos/{pagamentoId:guid}", Handle)
           .WithName("ConsultarPagamentoFornecedor")
           .WithTags("Fornecedor");

    private static Results<Ok<ConsultarPagamentoResponse>, NotFound> Handle(
        Guid pagamentoId,
        FornecedorSimulado fornecedor,
        ILogger<ConsultarPagamentoResponse> logger)
    {
        if (!fornecedor.TentarConsultar(pagamentoId, out var status))
        {
            logger.LogWarning("Pagamento nao encontrado no fornecedor pagamentoId={PagamentoId}", pagamentoId);
            return TypedResults.NotFound();
        }

        logger.LogInformation(
            "Consulta atendida pelo fornecedor pagamentoId={PagamentoId} status={Status}",
            pagamentoId, status);

        return TypedResults.Ok(new ConsultarPagamentoResponse(pagamentoId, status));
    }
}
