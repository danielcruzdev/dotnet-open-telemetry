using System.Diagnostics;
using System.Net.Http.Json;
using OpenTelemetry.Metrics;
using Pagamentos.Core.Tests.Features.Pagamentos.CriarPagamento;

namespace Pagamentos.Core.Tests;

public sealed class TelemetriaDoCoreTests
{
    private const string Rota = "/pagamentos";

    [Fact]
    public async Task Validacao_da_chave_tem_span_proprio_com_o_tipo()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();

        var span = app.Spans.Single(s => s.DisplayName == "ValidarChavePix");
        Assert.Equal("Email", span.GetTagItem("pix.chave.tipo"));
    }

    [Fact]
    public async Task Nenhum_span_carrega_a_chave_pix()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();

        // Atributo de span fica legivel para qualquer pessoa com acesso ao
        // dashboard, e chave PIX identifica uma pessoa.
        var valores = app.Spans.SelectMany(s => s.TagObjects)
            .Select(t => t.Value?.ToString() ?? string.Empty);
        Assert.DoesNotContain(valores, v => v.Contains("usuario@exemplo.com"));
    }

    [Fact]
    public async Task Chave_invalida_nao_gera_span_de_cliente()
    {
        using var app = new CoreApp();

        await app.CreateClient()
            .PostAsJsonAsync(Rota, new { chavePix = "invalida", valor = 150m, descricao = "x" });
        app.Flush();

        // Prova pelo trace que o Proxy nao foi chamado, nao so pelo contador.
        Assert.DoesNotContain(app.Spans, s => s.Kind == ActivityKind.Client);
    }

    [Fact]
    public async Task Chave_valida_gera_span_de_cliente_para_o_proxy()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();

        Assert.Contains(app.Spans, s => s.Kind == ActivityKind.Client);
    }

    [Fact]
    public async Task Recusa_de_saldo_nao_marca_o_span_do_servidor_como_erro()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(1000m));
        app.Flush();

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal(ActivityStatusCode.Unset, servidor.Status);
        Assert.Equal("saldo_insuficiente", servidor.GetTagItem("erro.motivo"));
    }

    [Fact]
    public async Task Falha_de_infraestrutura_marca_o_span_do_servidor()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(999.98m));
        app.Flush();

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal(ActivityStatusCode.Error, servidor.Status);
        // A descricao vem do nosso SetStatus; o Status sozinho a
        // instrumentacao ja daria para qualquer 5xx.
        Assert.Equal("fornecedor_indisponivel", servidor.StatusDescription);
    }

    [Fact]
    public async Task Metricas_de_pagamento_sao_publicadas()
    {
        using var app = new CoreApp();
        var client = app.CreateClient();

        await client.PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        await client.PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(1000m));
        app.Flush();

        Assert.Contains(app.Metricas, m => m.Name == "pagamentos.solicitados");
        Assert.Contains(app.Metricas, m => m.Name == "pagamentos.duracao");

        var status = TagsDe(app.Metricas.Single(m => m.Name == "pagamentos.solicitados"), "status");
        Assert.Contains("aprovado", status);
        Assert.Contains("recusado", status);
    }

    [Fact]
    public async Task Metricas_nao_usam_tag_de_alta_cardinalidade()
    {
        using var app = new CoreApp();

        await app.CreateClient().PostAsJsonAsync(Rota, CriarPagamentoTests.Pedido(150m));
        app.Flush();

        // Uma tag por requisicao cria uma serie temporal por requisicao e
        // derruba o backend. E o erro mais caro disponivel aqui.
        var proibidas = new[] { "correlation.id", "pagamento.id", "pix.chave", "chave" };
        foreach (var metrica in app.Metricas.Where(m => m.Name.StartsWith("pagamentos.")))
        {
            var chaves = ChavesDe(metrica);
            Assert.Empty(chaves.Intersect(proibidas));
        }
    }

    private static List<string> TagsDe(Metric metrica, string chave)
    {
        List<string> valores = [];
        foreach (ref readonly var ponto in metrica.GetMetricPoints())
            foreach (var tag in ponto.Tags)
                if (tag.Key == chave)
                    valores.Add(tag.Value!.ToString()!);
        return valores;
    }

    private static List<string> ChavesDe(Metric metrica)
    {
        List<string> chaves = [];
        foreach (ref readonly var ponto in metrica.GetMetricPoints())
            foreach (var tag in ponto.Tags)
                chaves.Add(tag.Key);
        return chaves;
    }
}
