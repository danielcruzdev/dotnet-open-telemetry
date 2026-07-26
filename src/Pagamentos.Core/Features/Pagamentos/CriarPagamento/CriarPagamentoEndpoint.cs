using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http.HttpResults;
using Pagamentos.Core.Infrastructure;

namespace Pagamentos.Core.Features.Pagamentos.CriarPagamento;

public sealed record CriarPagamentoRequest(string ChavePix, decimal Valor, string Descricao);

public sealed record CriarPagamentoResponse(Guid PagamentoId, string Status, string Autorizacao);

public sealed record FalhaResponse(string Status, string Motivo, string Detalhe);

public static class CriarPagamentoEndpoint
{
    private static readonly Counter<long> Solicitados =
        Telemetry.Meter.CreateCounter<long>("pagamentos.solicitados");

    private static readonly Histogram<double> Duracao =
        Telemetry.Meter.CreateHistogram<double>("pagamentos.duracao", unit: "s");

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/pagamentos", Handle)
           .WithName("CriarPagamento")
           .WithTags("Pagamentos");

    private static async Task<Results<Ok<CriarPagamentoResponse>, JsonHttpResult<FalhaResponse>>> Handle(
        CriarPagamentoRequest request,
        IFornecedorProxyClient fornecedor,
        ILogger<CriarPagamentoRequest> logger,
        CancellationToken cancellationToken)
    {
        var inicio = Stopwatch.GetTimestamp();

        if (!Validar(request.ChavePix, out var tipo))
        {
            logger.LogWarning("Pagamento recusado motivo={Motivo}", "chave_invalida");
            return Registrar(Recusado("chave_invalida", "A chave PIX informada nao e valida."), inicio);
        }

        logger.LogInformation(
            "Pagamento solicitado chavePixTipo={ChavePixTipo} valor={Valor}", tipo, request.Valor);

        var resultado = await fornecedor.ProcessarAsync(
            new PedidoAoFornecedor(request.ChavePix, request.Valor, request.Descricao),
            cancellationToken);

        return resultado switch
        {
            ResultadoProxy.Aprovado a => Registrar(Aprovado(a, logger), inicio),
            ResultadoProxy.Recusado r => Registrar(Recusado(r.Motivo, r.Detalhe, logger), inicio),
            ResultadoProxy.Falha f => Registrar(Falhou(f, logger), inicio),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Valida antes de chamar o fornecedor: gastar uma requisicao externa com
    /// uma chave que ja sabemos invalida nao ajuda ninguem. O span registra o
    /// tipo, nunca o valor da chave.
    /// </summary>
    private static bool Validar(string chavePix, out TipoChavePix tipo)
    {
        using var activity = Telemetry.Source.StartActivity("ValidarChavePix");

        var valida = ChavePix.TentarClassificar(chavePix, out tipo);

        activity?.SetTag("pix.chave.valida", valida);
        if (valida)
            activity?.SetTag("pix.chave.tipo", tipo.ToString());

        return valida;
    }

    private static Ok<CriarPagamentoResponse> Aprovado(ResultadoProxy.Aprovado aprovado, ILogger logger)
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
        // Recusa de negocio deixa o span Unset: marcar como erro afogaria as
        // indisponibilidades reais no painel.
        Activity.Current?.SetTag("pagamento.status", "recusado");
        Activity.Current?.SetTag("erro.motivo", motivo);

        logger?.LogWarning("Pagamento recusado motivo={Motivo}", motivo);

        return TypedResults.Json(
            new FalhaResponse("recusado", motivo, detalhe),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static JsonHttpResult<FalhaResponse> Falhou(ResultadoProxy.Falha falha, ILogger logger)
    {
        Activity.Current?.SetTag("erro.motivo", falha.Motivo);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, falha.Motivo);

        logger.LogError("Falha ao processar pagamento motivo={Motivo}", falha.Motivo);

        // O status e o motivo vem do hop de baixo sem traducao: um motivo que
        // virasse "erro interno" aqui apagaria a informacao que importa.
        return TypedResults.Json(
            new FalhaResponse("erro", falha.Motivo, falha.Detalhe), statusCode: falha.StatusCode);
    }

    private static T Registrar<T>(T resposta, long inicio)
        where T : IResult
    {
        var status = resposta is Ok<CriarPagamentoResponse> ? "aprovado"
            : Activity.Current?.GetTagItem("pagamento.status") as string ?? "erro";

        // Tags de baixa cardinalidade apenas: um id aqui criaria uma serie
        // temporal por requisicao.
        var tag = new KeyValuePair<string, object?>("status", status);
        Solicitados.Add(1, tag);
        Duracao.Record(Stopwatch.GetElapsedTime(inicio).TotalSeconds, tag);

        return resposta;
    }
}
