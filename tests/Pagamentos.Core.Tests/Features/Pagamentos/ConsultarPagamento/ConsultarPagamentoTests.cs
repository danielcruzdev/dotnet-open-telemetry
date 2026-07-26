using System.Net.Http.Json;
using System.Net;
using Pagamentos.Core.Tests;

namespace Pagamentos.Core.Tests.Features.Pagamentos.ConsultarPagamento;

public sealed class ConsultarPagamentoTests
{
    [Fact]
    public async Task Consulta_percorre_a_cadeia_ate_o_fornecedor()
    {
        using var app = new CoreApp();

        var response = await app.CreateClient().GetAsync($"/pagamentos/{CoreApp.PagamentoConhecido}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Consulta>();
        Assert.Equal(CoreApp.PagamentoConhecido, corpo!.PagamentoId);
        Assert.Equal("aprovado", corpo.Status);

        // O Core nao guarda estado proprio: a resposta veio do fornecedor.
        Assert.Contains("GET", app.ChamadasAoProxy);
    }

    [Fact]
    public async Task Pagamento_desconhecido_devolve_404()
    {
        using var app = new CoreApp();

        var response = await app.CreateClient().GetAsync($"/pagamentos/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record Consulta(Guid PagamentoId, string Status);
}
