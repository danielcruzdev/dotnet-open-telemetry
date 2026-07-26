using System.Net;
using System.Net.Http.Json;

namespace Pagamentos.Bff.Tests;

public sealed class ConsultarPagamentoTests
{
    [Fact]
    public async Task Consulta_percorre_a_cadeia()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().GetAsync($"/pagamentos/{BffApp.PagamentoConhecido}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<Consulta>();
        Assert.Equal(BffApp.PagamentoConhecido, corpo!.PagamentoId);
        Assert.Equal("aprovado", corpo.Status);
        Assert.Contains("GET", app.ChamadasAoCore);
    }

    [Fact]
    public async Task Pagamento_desconhecido_devolve_404()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().GetAsync($"/pagamentos/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record Consulta(Guid PagamentoId, string Status);
}
