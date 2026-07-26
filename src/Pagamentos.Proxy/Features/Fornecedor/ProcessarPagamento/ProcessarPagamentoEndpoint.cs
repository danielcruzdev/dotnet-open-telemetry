using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Pagamentos.Proxy.Infrastructure;

namespace Pagamentos.Proxy.Features.Fornecedor.ProcessarPagamento;

public sealed record ProcessarPagamentoRequest(string ChavePix, decimal Valor, string Descricao);

public sealed record ProcessarPagamentoResponse(Guid PagamentoId, string Status, string Autorizacao);

/// <summary>Mesma forma para toda recusa e toda falha: o Core so precisa
/// entender um formato, e o motivo atravessa os hops sem virar erro generico.</summary>
public sealed record FalhaResponse(string Status, string Motivo, string Detalhe);

public static class ProcessarPagamentoEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/fornecedor/pagamentos", Handle)
           .WithName("ProcessarPagamento")
           .WithTags("Fornecedor");

    private static async Task<Results<Ok<ProcessarPagamentoResponse>, JsonHttpResult<FalhaResponse>>> Handle(
        ProcessarPagamentoRequest request,
        FornecedorSimulado fornecedor,
        ILogger<ProcessarPagamentoRequest> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Encaminhando pagamento ao fornecedor fornecedor={Fornecedor} valor={Valor}",
            FornecedorSimulado.Nome, request.Valor);

        var resposta = await fornecedor.ProcessarAsync(request.Valor, cancellationToken);

        return resposta.Resultado switch
        {
            ResultadoFornecedor.Aprovado => Aprovado(resposta, logger),
            ResultadoFornecedor.SaldoInsuficiente => Recusado(logger),
            ResultadoFornecedor.Timeout => Falhou(
                logger, StatusCodes.Status504GatewayTimeout, "fornecedor_timeout",
                "O fornecedor nao respondeu no tempo limite."),
            _ => Falhou(
                logger, StatusCodes.Status502BadGateway, "fornecedor_indisponivel",
                "O fornecedor esta indisponivel."),
        };
    }

    private static Ok<ProcessarPagamentoResponse> Aprovado(RespostaFornecedor resposta, ILogger logger)
    {
        var pagamentoId = Guid.NewGuid();
        Activity.Current?.SetTag("pagamento.id", pagamentoId);
        Activity.Current?.SetTag("pagamento.status", "aprovado");

        logger.LogInformation(
            "Pagamento aprovado pelo fornecedor pagamentoId={PagamentoId}", pagamentoId);

        return TypedResults.Ok(
            new ProcessarPagamentoResponse(pagamentoId, "aprovado", resposta.Autorizacao!));
    }

    private static JsonHttpResult<FalhaResponse> Recusado(ILogger logger)
    {
        // Recusa de negocio nao e erro: o span fica Unset de proposito. Marcar
        // como erro afogaria as indisponibilidades reais no painel.
        Activity.Current?.SetTag("pagamento.status", "recusado");
        Activity.Current?.SetTag("erro.motivo", "saldo_insuficiente");

        logger.LogWarning("Pagamento recusado pelo fornecedor motivo={Motivo}", "saldo_insuficiente");

        return TypedResults.Json(
            new FalhaResponse("recusado", "saldo_insuficiente", "Saldo insuficiente para o valor solicitado."),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static JsonHttpResult<FalhaResponse> Falhou(
        ILogger logger, int statusCode, string motivo, string detalhe)
    {
        Activity.Current?.SetTag("erro.motivo", motivo);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, motivo);

        logger.LogError("Falha na chamada ao fornecedor motivo={Motivo}", motivo);

        return TypedResults.Json(new FalhaResponse("erro", motivo, detalhe), statusCode: statusCode);
    }
}
