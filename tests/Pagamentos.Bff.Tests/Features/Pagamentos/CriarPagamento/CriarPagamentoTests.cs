using System.Net.Http.Json;
using System.Net;
using Pagamentos.Bff.Tests;

namespace Pagamentos.Bff.Tests.Features.Pagamentos.CriarPagamento;

public sealed class CriarPagamentoTests
{
    private const string Rota = "/pagamentos";

    [Fact]
    public async Task Pagamento_valido_e_aprovado()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(150m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Aprovacao>();
        Assert.Equal("aprovado", corpo!.Status);
        Assert.Equal(BffApp.PagamentoConhecido, corpo.PagamentoId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Valor_nao_positivo_e_recusado_sem_chamar_o_core(decimal valor)
    {
        using var app = new BffApp();

        var response = await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = "usuario@exemplo.com", valor, descricao = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Falha>();
        Assert.Equal("valor_invalido", corpo!.Motivo);
        Assert.Empty(app.ChamadasAoCore);
    }

    [Fact]
    public async Task Chave_vazia_e_recusada_sem_chamar_o_core()
    {
        using var app = new BffApp();

        var response = await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = "", valor = 150m, descricao = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(app.ChamadasAoCore);
    }

    [Theory]
    [InlineData(1000, HttpStatusCode.UnprocessableEntity, "recusado", "saldo_insuficiente")]
    [InlineData(999.99, HttpStatusCode.GatewayTimeout, "erro", "fornecedor_timeout")]
    [InlineData(999.98, HttpStatusCode.BadGateway, "erro", "fornecedor_indisponivel")]
    public async Task Motivo_atravessa_os_tres_servicos_sem_degradar(
        decimal valor, HttpStatusCode esperado, string status, string motivo)
    {
        using var app = new BffApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, Pedido(valor));

        // Este e o criterio central do PRD: um motivo nascido no fornecedor
        // chega ao cliente sem virar "erro interno" pelo caminho.
        Assert.Equal(esperado, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Falha>();
        Assert.Equal(status, corpo!.Status);
        Assert.Equal(motivo, corpo.Motivo);
    }

    [Fact]
    public async Task Recusa_de_chave_do_core_chega_intacta()
    {
        using var app = new BffApp();

        var response = await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = "recusa-chave", valor = 150m, descricao = "x" });

        var corpo = await response.Content.ReadFromJsonAsync<Falha>();
        Assert.Equal("chave_invalida", corpo!.Motivo);
    }

    internal static object Pedido(decimal valor) =>
        new { chavePix = "usuario@exemplo.com", valor, descricao = "teste" };

    internal sealed record Aprovacao(Guid PagamentoId, string Status, string Autorizacao);

    internal sealed record Falha(string Status, string Motivo, string Detalhe);
}
