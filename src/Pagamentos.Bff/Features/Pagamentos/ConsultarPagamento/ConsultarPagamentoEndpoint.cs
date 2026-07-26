using Microsoft.AspNetCore.Http.HttpResults;
using Pagamentos.Bff.Infrastructure;

namespace Pagamentos.Bff.Features.Pagamentos.ConsultarPagamento;

public sealed record ConsultarPagamentoResponse(Guid PagamentoId, string Status);

public static class ConsultarPagamentoEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/pagamentos/{pagamentoId:guid}", Handle)
           .WithName("ConsultarPagamento")
           .WithTags("Pagamentos");

    private static async Task<Results<Ok<ConsultarPagamentoResponse>, NotFound>> Handle(
        Guid pagamentoId,
        IPagamentosCoreClient core,
        ILogger<ConsultarPagamentoResponse> logger,
        CancellationToken cancellationToken)
    {
        // O BFF nao guarda estado nem decide nada: repassa ao Core e adapta.
        var consulta = await core.ConsultarAsync(pagamentoId, cancellationToken);

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
