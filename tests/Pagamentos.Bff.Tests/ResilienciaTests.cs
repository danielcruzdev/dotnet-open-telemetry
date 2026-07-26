using System.Net.Http.Json;
using System.Net;
using Pagamentos.Bff.Tests.Features.Pagamentos.CriarPagamento;

namespace Pagamentos.Bff.Tests;

/// <summary>
/// Regressao de um bug achado so no AppHost: timeout e indisponibilidade
/// voltavam 500 com stack trace no corpo, depois de 30s, em vez do motivo.
/// A suite nao pegava porque o Core falso responde instantaneo.
/// </summary>
public sealed class ResilienciaTests
{
    private const string Rota = "/pagamentos";

    [Fact]
    public async Task Estouro_de_timeout_vira_motivo_e_nao_500()
    {
        using var app = new BffApp(timeoutTotal: TimeSpan.FromMilliseconds(300));

        var response = await app.CreateClient()
            .PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(BffApp.ValorQueDemora));

        // Polly lanca TimeoutRejectedException, que nao e HttpRequestException
        // nem TaskCanceledException. Sem trata-la, ela escapa como 500 e leva
        // junto toda a informacao de motivo.
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<CriarPagamentoTests.Falha>();
        Assert.Equal("core_timeout", corpo!.Motivo);
    }

    [Fact]
    public async Task Bff_nao_retenta_o_core()
    {
        using var app = new BffApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(999.98m));

        // O Core ja retenta o Proxy. Retentar de novo aqui multiplica as
        // tentativas (3 x 3) e a latencia, ate estourar o timeout total —
        // que foi exatamente o bug observado no AppHost.
        Assert.Single(app.ChamadasAoCore);
    }
}
