using System.Net;
using System.Net.Http.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Pagamentos.Bff.Infrastructure;

public sealed record PedidoAoCore(string ChavePix, decimal Valor, string Descricao);

public sealed record ConsultaDoCore(Guid PagamentoId, string Status);

public abstract record ResultadoCore
{
    public sealed record Aprovado(Guid PagamentoId, string Autorizacao) : ResultadoCore;

    public sealed record Recusado(string Motivo, string Detalhe) : ResultadoCore;

    public sealed record Falha(string Motivo, string Detalhe, int StatusCode) : ResultadoCore;
}

public interface IPagamentosCoreClient
{
    Task<ResultadoCore> CriarAsync(PedidoAoCore pedido, CancellationToken cancellationToken);

    Task<ConsultaDoCore?> ConsultarAsync(Guid pagamentoId, CancellationToken cancellationToken);
}

internal sealed class PagamentosCoreClient(HttpClient http) : IPagamentosCoreClient
{
    private const string Rota = "/pagamentos";

    public async Task<ResultadoCore> CriarAsync(PedidoAoCore pedido, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(Rota, pedido, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Motivo proprio para o Core inalcancavel: distinguir qual hop
            // caiu e o objetivo, e reaproveitar fornecedor_* aqui mentiria
            // sobre a origem.
            return new ResultadoCore.Falha("core_indisponivel", ex.Message, (int)HttpStatusCode.BadGateway);
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutRejectedException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // TimeoutRejectedException e da Polly: nao herda de
            // HttpRequestException nem de TaskCanceledException. Sem este
            // caso ela escapa como 500 com stack trace no corpo, e o motivo
            // — que atravessou tres servicos — se perde no ultimo metro.
            return new ResultadoCore.Falha(
                "core_timeout", "O Core nao respondeu no tempo limite.",
                (int)HttpStatusCode.GatewayTimeout);
        }
        catch (BrokenCircuitException)
        {
            return new ResultadoCore.Falha(
                "core_indisponivel", "O circuito para o Core esta aberto.",
                (int)HttpStatusCode.BadGateway);
        }

        if (response.IsSuccessStatusCode)
        {
            var aprovado = await response.Content.ReadFromJsonAsync<RespostaAprovada>(cancellationToken);

            return aprovado is null
                ? new ResultadoCore.Falha("core_resposta_invalida", "Resposta vazia do Core.", 502)
                : new ResultadoCore.Aprovado(aprovado.PagamentoId, aprovado.Autorizacao);
        }

        var falha = await response.Content.ReadFromJsonAsync<RespostaDeFalha>(cancellationToken);
        var motivo = falha?.Motivo ?? "core_resposta_invalida";
        var detalhe = falha?.Detalhe ?? "O Core respondeu em formato desconhecido.";

        return response.StatusCode is HttpStatusCode.UnprocessableEntity
            ? new ResultadoCore.Recusado(motivo, detalhe)
            : new ResultadoCore.Falha(motivo, detalhe, (int)response.StatusCode);
    }

    public async Task<ConsultaDoCore?> ConsultarAsync(Guid pagamentoId, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"{Rota}/{pagamentoId}", cancellationToken);

        return response.StatusCode is HttpStatusCode.NotFound
            ? null
            : await response.Content.ReadFromJsonAsync<ConsultaDoCore>(cancellationToken);
    }

    private sealed record RespostaAprovada(Guid PagamentoId, string Status, string Autorizacao);

    private sealed record RespostaDeFalha(string Status, string Motivo, string Detalhe);
}
