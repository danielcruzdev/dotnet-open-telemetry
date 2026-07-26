using System.Diagnostics;

namespace Shared.Observability.Tests;

/// <summary>
/// Continuidade do trace ao atravessar um hop. Se o servico ignorar o
/// traceparent que chega, cada servico vira um trace separado — os spans
/// existem, nada falha, e a requisicao simplesmente nao pode mais ser
/// seguida de ponta a ponta.
/// </summary>
public sealed class ContinuidadeDeTraceTests
{
    [Fact]
    public async Task Trace_recebido_e_continuado_em_vez_de_recomecado()
    {
        await using var app = TestApp.Criar();
        var traceId = ActivityTraceId.CreateRandom();
        var spanIdPai = ActivitySpanId.CreateRandom();

        var request = new HttpRequestMessage(HttpMethod.Get, TestApp.Rota);
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{spanIdPai}-01");

        await app.Client.SendAsync(request);
        app.Flush();

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal(traceId, servidor.TraceId);
        Assert.Equal(spanIdPai, servidor.ParentSpanId);
    }

    [Fact]
    public async Task Trace_recebido_alcanca_tambem_os_spans_filhos()
    {
        await using var app = TestApp.Criar();
        var traceId = ActivityTraceId.CreateRandom();
        var spanIdPai = ActivitySpanId.CreateRandom();

        var request = new HttpRequestMessage(HttpMethod.Get, TestApp.Rota);
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{spanIdPai}-01");

        await app.Client.SendAsync(request);
        app.Flush();

        // Span de negocio e span de saida no mesmo trace: e o que faz a
        // arvore inteira aparecer sob um unico trace no dashboard.
        Assert.NotEmpty(app.Spans);
        Assert.All(app.Spans, s => Assert.Equal(traceId, s.TraceId));
    }

    [Fact]
    public async Task Sem_traceparent_o_servico_inicia_um_trace_proprio()
    {
        await using var app = TestApp.Criar();

        await app.ChamarAsync(null);

        var servidor = app.Spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.NotEqual(default, servidor.TraceId);
        Assert.Equal(default, servidor.ParentSpanId);
    }
}
