using Microsoft.AspNetCore.Http.HttpResults;
using Pagamentos.Core.Infrastructure;

namespace Pagamentos.Core.Features.Pagamentos.ConsultarPagamento;

public sealed record ConsultarPagamentoResponse(Guid PagamentoId, string Status);

public static class ConsultarPagamentoEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/pagamentos/{pagamentoId:guid}", Handle)
           .WithName("ConsultarPagamento")
           .WithTags("Pagamentos");

    private static async Task<Results<Ok<ConsultarPagamentoResponse>, NotFound>> Handle(
        Guid pagamentoId,
        IFornecedorProxyClient fornecedor,
        ILogger<ConsultarPagamentoResponse> logger,
        CancellationToken cancellationToken)
    {
        // O Core nao guarda estado proprio: quem emite o pagamentoId e o
        // fornecedor, entao duplicar o razao aqui criaria duas verdades.
        var consulta = await fornecedor.ConsultarAsync(pagamentoId, cancellationToken);

        if (consulta is null)
        {
            logger.LogWarning("Pagamento nao encontrado pagamentoId={PagamentoId}", pagamentoId);
            return TypedResults.NotFound();
        }

        logger.LogInformation(
            "Consulta atendida pagamentoId={PagamentoId} status={Status}", pagamentoId, consulta.Status);

        return TypedResults.Ok(new ConsultarPagamentoResponse(consulta.PagamentoId, consulta.Status));
    }
}
