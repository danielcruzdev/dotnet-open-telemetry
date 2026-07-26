using System.Diagnostics;
using System.Net.Http.Json;
using OpenTelemetry.Metrics;
using Pagamentos.Proxy.Tests.Features.Fornecedor.ProcessarPagamento;

namespace Pagamentos.Proxy.Tests;

/// <summary>
/// O Proxy e onde a maioria das investigacoes termina, entao o trace dele
/// precisa dizer o que aconteceu sem ambiguidade.
/// </summary>
public sealed class TelemetriaDoProxyTests
{
    private const string Rota = "/fornecedor/pagamentos";

    [Fact]
    public async Task Span_do_fornecedor_fica_aninhado_no_span_do_servidor()
    {
        using var app = new ProxyApp();

        await app.CreateClient().PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(150m));
        app.Flush();

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        var fornecedor = app.SpanDoFornecedor();

        Assert.Equal(servidor.SpanId, fornecedor.ParentSpanId);
        Assert.Equal(servidor.TraceId, fornecedor.TraceId);
    }

    [Fact]
    public async Task Span_do_fornecedor_identifica_o_parceiro()
    {
        using var app = new ProxyApp();

        await app.CreateClient().PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(150m));
        app.Flush();

        Assert.Equal("banco-parceiro", app.SpanDoFornecedor().GetTagItem("fornecedor.nome"));
    }

    [Fact]
    public async Task Recusa_de_saldo_nao_marca_o_span_como_erro()
    {
        using var app = new ProxyApp();

        await app.CreateClient().PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(1000m));
        app.Flush();

        // Se recusa de negocio virasse erro, o painel de erros ficaria
        // inutil e as indisponibilidades reais sumiriam no ruido.
        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal(ActivityStatusCode.Unset, servidor.Status);
        Assert.Equal("saldo_insuficiente", servidor.GetTagItem("erro.motivo"));
    }

    [Theory]
    [InlineData(999.99, "fornecedor_timeout")]
    [InlineData(999.98, "fornecedor_indisponivel")]
    public async Task Falha_de_infraestrutura_marca_o_span_como_erro(decimal valor, string motivo)
    {
        using var app = new ProxyApp();

        await app.CreateClient().PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(valor));
        app.Flush();

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal(ActivityStatusCode.Error, servidor.Status);
        Assert.Equal(motivo, servidor.GetTagItem("erro.motivo"));

        // A instrumentacao do ASP.NET Core ja marca qualquer 5xx como Error,
        // entao so o Status nao prova nada sobre o nosso codigo. A descricao
        // vem do nosso SetStatus e e o que torna esta asercao falseavel.
        Assert.Equal(motivo, servidor.StatusDescription);
    }

    [Fact]
    public async Task Aprovacao_registra_o_status_no_span()
    {
        using var app = new ProxyApp();

        await app.CreateClient().PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(150m));
        app.Flush();

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal("aprovado", servidor.GetTagItem("pagamento.status"));
    }

    [Fact]
    public async Task Nenhum_span_carrega_a_chave_pix()
    {
        using var app = new ProxyApp();

        await app.CreateClient().PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(150m));
        app.Flush();

        // Atributo de span e armazenado sem redacao e fica visivel para
        // qualquer pessoa com acesso ao dashboard.
        var valores = app.Spans.SelectMany(s => s.TagObjects)
            .Select(t => t.Value?.ToString() ?? string.Empty);
        Assert.DoesNotContain(valores, v => v.Contains("usuario@exemplo.com"));
    }

    [Fact]
    public async Task Metrica_de_chamadas_separa_por_resultado()
    {
        using var app = new ProxyApp();
        var client = app.CreateClient();

        await client.PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(150m));
        await client.PostAsJsonAsync(Rota, ProcessarPagamentoTests.Pedido(1000m));
        app.Flush();

        var metrica = app.Metricas.Single(m => m.Name == "fornecedor.chamadas");
        var resultados = new List<string>();
        foreach (ref readonly var ponto in metrica.GetMetricPoints())
            foreach (var tag in ponto.Tags)
                if (tag.Key == "resultado")
                    resultados.Add(tag.Value!.ToString()!);

        Assert.Contains("Aprovado", resultados);
        Assert.Contains("SaldoInsuficiente", resultados);
    }
}
