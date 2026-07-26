using System.Diagnostics;
using System.Net.Http.Json;
using OpenTelemetry.Logs;
using Shared.Observability;

namespace Pagamentos.Bff.Tests;

/// <summary>
/// O BFF e a porta de entrada, entao e onde o correlationId normalmente
/// nasce. O log de entrada dele e a ancora de qualquer investigacao.
/// </summary>
public sealed class CorrelacaoNoBffTests
{
    private const string Rota = "/pagamentos";

    [Fact]
    public async Task Requisicao_sem_header_ganha_um_id_novo()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));

        var id = Header(response);
        Assert.True(CorrelationId.IsValid(id));
    }

    [Fact]
    public async Task Requisicao_com_header_preserva_o_id()
    {
        using var app = new BffApp();
        var request = new HttpRequestMessage(HttpMethod.Post, Rota)
        {
            Content = JsonContent.Create(CriarPagamentoTests.Pedido(150m)),
        };
        request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, "veio-de-fora");

        var response = await app.CreateClient().SendAsync(request);

        Assert.Equal("veio-de-fora", Header(response));
    }

    [Fact]
    public async Task Id_gerado_pelo_bff_chega_ao_core()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        var id = Header(response);

        // O mesmo id que volta ao cliente e o que seguiu para o proximo hop.
        Assert.Equal(id, app.HeadersRecebidos[CorrelationId.HeaderName]);
    }

    [Fact]
    public async Task Log_de_entrada_carrega_o_mesmo_id_devolvido_ao_cliente()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();
        var id = Header(response);

        // Sem essa ligacao entre o header devolvido e o log, o cliente teria
        // um id que nao encontra nada — a investigacao nao teria por onde
        // comecar.
        // TraceId preenchido = log emitido dentro da requisicao. Os de
        // startup (Microsoft.Hosting.Lifetime) nascem fora de qualquer
        // requisicao e nao tem — nem poderiam ter — correlationId.
        // Hosting.Diagnostics fica de fora pelo limite ja documentado na
        // skill correlation-id: roda fora do pipeline de middleware.
        var daRequisicao = app.Logs
            .Where(l => l.TraceId != default)
            .Where(l => l.CategoryName != "Microsoft.AspNetCore.Hosting.Diagnostics")
            .ToList();

        Assert.NotEmpty(daRequisicao);
        Assert.All(daRequisicao, log => Assert.Equal(id, CorrelationIdDoLog(log)));
    }

    [Fact]
    public async Task Chamada_ao_core_leva_traceparent_ligado_ao_span_de_cliente()
    {
        using var app = new BffApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();

        var cliente = app.Spans.Single(s => s.Kind == ActivityKind.Client);

        // Prova a ligacao pai-filho entre os hops pelo proprio traceparent
        // que chegou do outro lado, e nao por inferencia.
        Assert.Equal(cliente.TraceId.ToString(), app.TraceIdRecebido);
        Assert.Equal(cliente.SpanId.ToString(), app.SpanIdPaiRecebido);
    }

    [Fact]
    public async Task Existe_log_de_entrada_antes_de_qualquer_decisao()
    {
        using var app = new BffApp();

        var response = await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();

        // A tarefa 4.4 pede este log especificamente: e o primeiro registro
        // que existe para um correlationId, e sem ele uma requisicao que
        // morre logo no comeco nao deixa rastro nenhum em log.
        var entrada = app.Logs.Where(l => l.TraceId != default)
            .FirstOrDefault(l => l.Body?.Contains("Requisicao de pagamento recebida") == true);

        Assert.NotNull(entrada);
        Assert.Equal(Header(response), CorrelationIdDoLog(entrada));
    }

    [Fact]
    public async Task Recusa_de_formato_registra_log_de_entrada_mesmo_assim()
    {
        using var app = new BffApp();

        await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = "", valor = 0m, descricao = "x" });
        app.Flush();

        // Recusada no proprio BFF, sem nunca chegar ao Core: ainda assim a
        // requisicao precisa aparecer na investigacao.
        Assert.Contains(app.Logs, l => l.Body?.Contains("Requisicao de pagamento recebida") == true);
    }

    [Fact]
    public async Task Recusa_de_formato_nao_gera_span_de_cliente()
    {
        using var app = new BffApp();

        await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = "", valor = 0m, descricao = "x" });
        app.Flush();

        Assert.DoesNotContain(app.Spans, s => s.Kind == ActivityKind.Client);
    }

    private static string? Header(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CorrelationId.HeaderName, out var v) ? v.FirstOrDefault() : null;

    private static string? CorrelationIdDoLog(LogRecord log)
    {
        string? encontrado = null;
        log.ForEachScope(
            (scope, _) =>
            {
                foreach (var item in scope)
                    if (item.Key == CorrelationId.LogPropertyName)
                        encontrado = item.Value as string;
            },
            default(object));
        return encontrado;
    }
}
