using System.Net;
using System.Net.Http.Json;

namespace Pagamentos.Core.Tests;

public sealed class CriarPagamentoTests
{
    private const string Rota = "/pagamentos";

    [Fact]
    public async Task Pagamento_valido_e_aprovado()
    {
        using var app = new CoreApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(150m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Aprovacao>();
        Assert.Equal("aprovado", corpo!.Status);
        Assert.Equal(CoreApp.PagamentoConhecido, corpo.PagamentoId);
    }

    [Theory]
    [InlineData("sem-arroba")]
    [InlineData("123")]
    [InlineData("")]
    public async Task Chave_invalida_e_recusada_sem_chamar_o_proxy(string chave)
    {
        using var app = new CoreApp();

        var response = await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = chave, valor = 150m, descricao = "teste" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Falha>();
        Assert.Equal("chave_invalida", corpo!.Motivo);

        // A validacao para no Core: chamar o fornecedor com uma chave que ja
        // sabemos invalida gasta uma requisicao externa a toa.
        Assert.Empty(app.ChamadasAoProxy);
    }

    [Fact]
    public async Task Motivo_de_recusa_do_proxy_chega_intacto()
    {
        using var app = new CoreApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(1000m));

        // Um motivo que degrada para "erro interno" um hop acima e exatamente
        // o problema que este projeto existe para resolver.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Falha>();
        Assert.Equal("recusado", corpo!.Status);
        Assert.Equal("saldo_insuficiente", corpo.Motivo);
    }

    [Theory]
    [InlineData(999.99, HttpStatusCode.GatewayTimeout, "fornecedor_timeout")]
    [InlineData(999.98, HttpStatusCode.BadGateway, "fornecedor_indisponivel")]
    public async Task Falha_do_proxy_preserva_o_motivo(decimal valor, HttpStatusCode esperado, string motivo)
    {
        using var app = new CoreApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(valor));

        Assert.Equal(esperado, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Falha>();
        Assert.Equal(motivo, corpo!.Motivo);
    }

    [Fact]
    public async Task Falha_de_infraestrutura_e_retentada_antes_de_desistir()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, Pedido(999.98m));

        // O PRD exige "502 apos retries". Cada tentativa vira um span de
        // cliente irmao no trace, e e assim que o retry fica visivel na
        // investigacao em vez de parecer uma unica chamada lenta.
        Assert.True(app.ChamadasAoProxy.Count > 1,
            $"esperava mais de uma tentativa, houve {app.ChamadasAoProxy.Count}");
    }

    [Fact]
    public async Task Recusa_de_negocio_nao_e_retentada()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, Pedido(1000m));

        // 422 e resposta definitiva: repetir so multiplicaria a carga.
        Assert.Single(app.ChamadasAoProxy);
    }

    internal static object Pedido(decimal valor) =>
        new { chavePix = "usuario@exemplo.com", valor, descricao = "teste" };

    internal sealed record Aprovacao(Guid PagamentoId, string Status, string Autorizacao);

    internal sealed record Falha(string Status, string Motivo, string Detalhe);
}
