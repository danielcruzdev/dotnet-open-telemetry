using System.Net;
using System.Net.Http.Json;

namespace Pagamentos.Core.Infrastructure;

public sealed record PedidoAoFornecedor(string ChavePix, decimal Valor, string Descricao);

public sealed record ConsultaDoFornecedor(Guid PagamentoId, string Status);

/// <summary>Desfecho da chamada ao Proxy. O cliente so transporta e
/// desserializa; quem decide o que fazer com cada caso e o slice.</summary>
public abstract record ResultadoProxy
{
    public sealed record Aprovado(Guid PagamentoId, string Autorizacao) : ResultadoProxy;

    public sealed record Recusado(string Motivo, string Detalhe) : ResultadoProxy;

    public sealed record Falha(string Motivo, string Detalhe, int StatusCode) : ResultadoProxy;
}

public interface IFornecedorProxyClient
{
    Task<ResultadoProxy> ProcessarAsync(PedidoAoFornecedor pedido, CancellationToken cancellationToken);

    Task<ConsultaDoFornecedor?> ConsultarAsync(Guid pagamentoId, CancellationToken cancellationToken);
}

internal sealed class FornecedorProxyClient(HttpClient http) : IFornecedorProxyClient
{
    private const string Rota = "/fornecedor/pagamentos";

    public async Task<ResultadoProxy> ProcessarAsync(
        PedidoAoFornecedor pedido, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(Rota, pedido, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Motivo proprio: o Proxy inalcancavel e um problema diferente do
            // fornecedor recusando, e localizar o hop e o objetivo aqui.
            return new ResultadoProxy.Falha(
                "proxy_indisponivel", ex.Message, (int)HttpStatusCode.BadGateway);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ResultadoProxy.Falha(
                "proxy_timeout", "O Proxy nao respondeu no tempo limite.",
                (int)HttpStatusCode.GatewayTimeout);
        }

        if (response.IsSuccessStatusCode)
        {
            var aprovado = await response.Content
                .ReadFromJsonAsync<RespostaAprovada>(cancellationToken);

            return aprovado is null
                ? new ResultadoProxy.Falha("proxy_resposta_invalida", "Resposta vazia do Proxy.", 502)
                : new ResultadoProxy.Aprovado(aprovado.PagamentoId, aprovado.Autorizacao);
        }

        var falha = await response.Content.ReadFromJsonAsync<RespostaDeFalha>(cancellationToken);
        var motivo = falha?.Motivo ?? "proxy_resposta_invalida";
        var detalhe = falha?.Detalhe ?? "O Proxy respondeu em formato desconhecido.";

        // 422 e recusa de negocio; o resto e falha de infraestrutura.
        return response.StatusCode is HttpStatusCode.UnprocessableEntity
            ? new ResultadoProxy.Recusado(motivo, detalhe)
            : new ResultadoProxy.Falha(motivo, detalhe, (int)response.StatusCode);
    }

    public async Task<ConsultaDoFornecedor?> ConsultarAsync(
        Guid pagamentoId, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"{Rota}/{pagamentoId}", cancellationToken);

        return response.StatusCode is HttpStatusCode.NotFound
            ? null
            : await response.Content.ReadFromJsonAsync<ConsultaDoFornecedor>(cancellationToken);
    }

    private sealed record RespostaAprovada(Guid PagamentoId, string Status, string Autorizacao);

    private sealed record RespostaDeFalha(string Status, string Motivo, string Detalhe);
}
