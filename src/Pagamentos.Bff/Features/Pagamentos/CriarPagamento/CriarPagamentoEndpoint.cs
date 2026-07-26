using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Pagamentos.Bff.Infrastructure;

namespace Pagamentos.Bff.Features.Pagamentos.CriarPagamento;

public sealed record CriarPagamentoRequest(string ChavePix, decimal Valor, string Descricao);

public sealed record CriarPagamentoResponse(Guid PagamentoId, string Status, string Autorizacao);

public sealed record FalhaResponse(string Status, string Motivo, string Detalhe);

public static class CriarPagamentoEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/pagamentos", Handle)
           .WithName("CriarPagamento")
           .WithTags("Pagamentos");

    private static async Task<Results<Ok<CriarPagamentoResponse>, JsonHttpResult<FalhaResponse>>> Handle(
        CriarPagamentoRequest request,
        IPagamentosCoreClient core,
        ILogger<CriarPagamentoRequest> logger,
        CancellationToken cancellationToken)
    {
        // Log de entrada: e a ancora da investigacao. O correlationId ja veio
        // no escopo pelo middleware, e e o mesmo que o cliente recebe de volta
        // no header — e assim que um id em maos encontra a requisicao.
        logger.LogInformation("Requisicao de pagamento recebida valor={Valor}", request.Valor);

        // So formato: a regra de negocio da chave PIX pertence ao Core.
        if (Invalido(request, out var motivo, out var detalhe))
        {
            logger.LogWarning("Requisicao recusada no BFF motivo={Motivo}", motivo);
            return Recusado(motivo, detalhe);
        }

        var resultado = await core.CriarAsync(
            new PedidoAoCore(request.ChavePix, request.Valor, request.Descricao), cancellationToken);

        return resultado switch
        {
            ResultadoCore.Aprovado a => Aprovado(a, logger),
            ResultadoCore.Recusado r => Recusado(r.Motivo, r.Detalhe, logger),
            ResultadoCore.Falha f => Falhou(f, logger),
            _ => throw new UnreachableException(),
        };
    }

    private static bool Invalido(CriarPagamentoRequest request, out string motivo, out string detalhe)
    {
        if (request.Valor <= 0)
        {
            motivo = "valor_invalido";
            detalhe = "O valor do pagamento deve ser maior que zero.";
            return true;
        }

        if (string.IsNullOrWhiteSpace(request.ChavePix))
        {
            motivo = "chave_ausente";
            detalhe = "A chave PIX e obrigatoria.";
            return true;
        }

        motivo = detalhe = string.Empty;
        return false;
    }

    private static Ok<CriarPagamentoResponse> Aprovado(ResultadoCore.Aprovado aprovado, ILogger logger)
    {
        Activity.Current?.SetTag("pagamento.id", aprovado.PagamentoId);
        Activity.Current?.SetTag("pagamento.status", "aprovado");

        logger.LogInformation("Pagamento aprovado pagamentoId={PagamentoId}", aprovado.PagamentoId);

        return TypedResults.Ok(
            new CriarPagamentoResponse(aprovado.PagamentoId, "aprovado", aprovado.Autorizacao));
    }

    private static JsonHttpResult<FalhaResponse> Recusado(
        string motivo, string detalhe, ILogger? logger = null)
    {
        Activity.Current?.SetTag("pagamento.status", "recusado");
        Activity.Current?.SetTag("erro.motivo", motivo);

        logger?.LogWarning("Pagamento recusado motivo={Motivo}", motivo);

        return TypedResults.Json(
            new FalhaResponse("recusado", motivo, detalhe),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static JsonHttpResult<FalhaResponse> Falhou(ResultadoCore.Falha falha, ILogger logger)
    {
        Activity.Current?.SetTag("erro.motivo", falha.Motivo);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, falha.Motivo);

        logger.LogError("Falha ao processar pagamento motivo={Motivo}", falha.Motivo);

        // Ultimo hop antes do cliente: e aqui que um motivo degradado para
        // "erro interno" apagaria de vez a informacao que veio de tres
        // servicos abaixo.
        return TypedResults.Json(
            new FalhaResponse("erro", falha.Motivo, falha.Detalhe), statusCode: falha.StatusCode);
    }
}
