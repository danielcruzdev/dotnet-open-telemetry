using System.Diagnostics;
using OpenTelemetry.Logs;

namespace Shared.Observability.Tests;

/// <summary>
/// Os seis passos do contrato de correlacao, verificados numa requisicao real.
/// </summary>
public sealed class ContratoDeCorrelacaoTests
{
    // Passo 1 — entrada

    [Fact]
    public async Task Id_recebido_no_header_e_preservado()
    {
        await using var app = TestApp.Criar();

        var response = await app.ChamarAsync("teste-123");

        Assert.Equal("teste-123", Header(response));
    }

    [Fact]
    public async Task Id_ausente_e_gerado()
    {
        await using var app = TestApp.Criar();

        var response = await app.ChamarAsync(null);

        Assert.True(CorrelationId.IsValid(Header(response)));
    }

    [Fact]
    public async Task Id_invalido_e_descartado_e_substituido()
    {
        await using var app = TestApp.Criar();

        var response = await app.ChamarAsync("\"; DROP TABLE pagamentos");

        var devolvido = Header(response);
        Assert.NotEqual("\"; DROP TABLE pagamentos", devolvido);
        Assert.True(CorrelationId.IsValid(devolvido));
    }

    // Passo 5 — response

    [Fact]
    public async Task Response_sempre_carrega_o_header()
    {
        await using var app = TestApp.Criar();

        var response = await app.ChamarAsync("teste-123");

        Assert.True(response.Headers.Contains(CorrelationId.HeaderName));
    }

    // Passo 3 — todo span, nao so o raiz

    [Fact]
    public async Task Todo_span_carrega_o_correlation_id()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        Assert.NotEmpty(app.Spans);
        var semTag = app.Spans
            .Where(s => s.GetTagItem(CorrelationId.TagName) as string != "teste-123")
            .Select(s => s.DisplayName)
            .ToList();
        Assert.Empty(semTag);
    }

    [Fact]
    public async Task Span_raiz_do_servidor_carrega_o_correlation_id()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        // O span do servidor comeca ANTES do middleware, entao o processor
        // sozinho nao o alcanca. Este teste protege esse caso especifico.
        var raiz = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal("teste-123", raiz.GetTagItem(CorrelationId.TagName) as string);
    }

    [Fact]
    public async Task Span_de_negocio_e_span_de_saida_carregam_o_correlation_id()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        var negocio = app.Spans.Single(s => s.DisplayName == "SpanDeNegocio");
        var saida = app.Spans.Single(s => s.Kind == ActivityKind.Client);
        Assert.Equal("teste-123", negocio.GetTagItem(CorrelationId.TagName) as string);
        Assert.Equal("teste-123", saida.GetTagItem(CorrelationId.TagName) as string);
    }

    // Passo 4 — todo log

    [Fact]
    public async Task Todo_log_de_aplicacao_carrega_o_correlation_id()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        var deAplicacao = app.Logs.Where(l => l.CategoryName != CategoriaDoHosting).ToList();
        Assert.NotEmpty(deAplicacao);
        Assert.All(deAplicacao, log => Assert.Equal("teste-123", CorrelationIdDoLog(log)));
    }

    [Fact]
    public async Task Logs_do_proprio_hosting_ficam_fora_do_escopo()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        // Limite conhecido e inevitavel: "Request starting" e "Request
        // finished" sao emitidos fora do pipeline de middleware, entao
        // nenhum escopo os alcanca. Fixado em teste para que a ausencia
        // do id nesses dois logs nunca seja lida como regressao.
        var doHosting = app.Logs.Where(l => l.CategoryName == CategoriaDoHosting).ToList();
        Assert.NotEmpty(doHosting);
        Assert.All(doHosting, log => Assert.Null(CorrelationIdDoLog(log)));
    }

    // Passo 6 — saida

    [Fact]
    public async Task Chamada_de_saida_leva_o_header_de_correlacao()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        Assert.Equal("teste-123", app.HeadersDeSaida[CorrelationId.HeaderName]);
    }

    [Fact]
    public async Task Chamada_de_saida_leva_traceparent_e_baggage()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync("teste-123");

        Assert.True(app.HeadersDeSaida.ContainsKey("traceparent"));
        Assert.Contains("correlation.id=teste-123", app.HeadersDeSaida["baggage"]);
    }

    private const string CategoriaDoHosting = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private static string? Header(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CorrelationId.HeaderName, out var values)
            ? values.FirstOrDefault()
            : null;

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
