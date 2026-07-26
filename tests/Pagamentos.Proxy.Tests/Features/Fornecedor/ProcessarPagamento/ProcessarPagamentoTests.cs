using System.Net.Http.Json;
using System.Net;
using Pagamentos.Proxy.Tests;

namespace Pagamentos.Proxy.Tests.Features.Fornecedor.ProcessarPagamento;

/// <summary>
/// A matriz de desfechos da secao 4 do PRD. Os gatilhos sao deterministicos
/// por valor: investigar um problema exige poder reproduzi-lo.
/// </summary>
public sealed class ProcessarPagamentoTests
{
    private const string Rota = "/fornecedor/pagamentos";

    [Theory]
    [InlineData(1)]
    [InlineData(150.00)]
    [InlineData(999.96)]
    public async Task Valor_abaixo_do_limite_e_aprovado(decimal valor)
    {
        using var app = new ProxyApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(valor));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Aprovacao>();
        Assert.Equal("aprovado", corpo!.Status);
        Assert.NotEqual(Guid.Empty, corpo.PagamentoId);
        Assert.False(string.IsNullOrWhiteSpace(corpo.Autorizacao));
    }

    [Theory]
    [InlineData(1000.00)]
    [InlineData(5000.00)]
    public async Task Valor_no_limite_ou_acima_e_recusado_por_saldo(decimal valor)
    {
        using var app = new ProxyApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(valor));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Recusa>();
        Assert.Equal("recusado", corpo!.Status);
        Assert.Equal("saldo_insuficiente", corpo.Motivo);
    }

    [Fact]
    public async Task Valor_gatilho_de_timeout_devolve_504()
    {
        using var app = new ProxyApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(999.99m));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Recusa>();
        Assert.Equal("fornecedor_timeout", corpo!.Motivo);
    }

    [Fact]
    public async Task Valor_gatilho_de_indisponibilidade_devolve_502()
    {
        using var app = new ProxyApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(999.98m));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Recusa>();
        Assert.Equal("fornecedor_indisponivel", corpo!.Motivo);
    }

    [Fact]
    public async Task Valor_gatilho_de_latencia_e_aprovado_porem_lento()
    {
        using var app = new ProxyApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(999.97m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Aprovacao>();
        Assert.Equal("aprovado", corpo!.Status);
    }

    [Fact]
    public async Task Gatilhos_sao_deterministicos_entre_chamadas()
    {
        using var app = new ProxyApp();
        var client = app.CreateClient();

        // Falha aleatoria nao serve para demonstrar tracing: o cenario
        // precisa ser reproduzivel na hora de investigar.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsJsonAsync(Rota, Pedido(150m))).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await client.PostAsJsonAsync(Rota, Pedido(1000m))).StatusCode);
            Assert.Equal(HttpStatusCode.GatewayTimeout,
                (await client.PostAsJsonAsync(Rota, Pedido(999.99m))).StatusCode);
            Assert.Equal(HttpStatusCode.BadGateway,
                (await client.PostAsJsonAsync(Rota, Pedido(999.98m))).StatusCode);
        }
    }

    internal static object Pedido(decimal valor) =>
        new { chavePix = "usuario@exemplo.com", valor, descricao = "teste" };

    internal sealed record Aprovacao(Guid PagamentoId, string Status, string Autorizacao);

    internal sealed record Recusa(string Status, string Motivo, string Detalhe);
}
