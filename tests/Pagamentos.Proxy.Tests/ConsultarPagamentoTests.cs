using System.Net;
using System.Net.Http.Json;

namespace Pagamentos.Proxy.Tests;

/// <summary>
/// O fornecedor e quem gera o pagamentoId, entao e ele quem responde pela
/// consulta. Guardar o mesmo estado tambem no Core so criaria duas versoes
/// da verdade.
/// </summary>
public sealed class ConsultarPagamentoTests
{
    private const string Rota = "/fornecedor/pagamentos";

    [Fact]
    public async Task Pagamento_aprovado_pode_ser_consultado_depois()
    {
        using var app = new ProxyApp();
        var client = app.CreateClient();

        var criado = await (await client.PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(150m)))
            .Content.ReadFromJsonAsync<ProcessarPagamentoTests.Aprovacao>();

        var response = await client.GetAsync($"{Rota}/{criado!.PagamentoId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var consulta = await response.Content.ReadFromJsonAsync<Consulta>();
        Assert.Equal(criado.PagamentoId, consulta!.PagamentoId);
        Assert.Equal("aprovado", consulta.Status);
    }

    [Fact]
    public async Task Pagamento_desconhecido_devolve_404()
    {
        using var app = new ProxyApp();

        var response = await app.CreateClient().GetAsync($"{Rota}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pagamento_recusado_nao_fica_consultavel()
    {
        using var app = new ProxyApp();
        var client = app.CreateClient();

        // Recusa por saldo nao gera pagamento, entao nao ha o que consultar.
        await client.PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(1000m));
        var response = await client.GetAsync($"{Rota}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    internal sealed record Consulta(Guid PagamentoId, string Status);
}
