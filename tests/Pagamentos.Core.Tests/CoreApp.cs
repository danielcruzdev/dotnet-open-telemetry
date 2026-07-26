using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Pagamentos.Core.Tests;

/// <summary>
/// Sobe o Core com um Proxy falso em Kestrel real, resolvido pelo mesmo
/// service discovery de producao. Precisa ser um servidor de verdade:
/// substituir o primary handler tiraria o DiagnosticsHandler do caminho e
/// com ele o span de cliente — justamente o que alguns testes checam.
/// </summary>
internal sealed class CoreApp : WebApplicationFactory<Program>
{
    private readonly WebApplication _proxyFalso;

    public List<Activity> Spans { get; } = [];
    public List<Metric> Metricas { get; } = [];
    public List<string> ChamadasAoProxy { get; } = [];

    public CoreApp()
    {
        _proxyFalso = CriarProxyFalso();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Chave do service discovery do Aspire: e assim que
        // "https+http://pagamentos-proxy" e resolvido.
        var endereco = new Uri(EnderecoDe(_proxyFalso));
        builder.UseSetting("services:pagamentos-proxy:http:0", endereco.ToString());

        builder.ConfigureTestServices(services =>
        {
            // O retry e comportamento de producao exigido pelo PRD, mas o
            // backoff exponencial padrao custa dezenas de segundos na suite.
            // ConfigureAll alcanca todo cliente nomeado de uma vez.
            services.ConfigureAll<HttpStandardResilienceOptions>(o =>
            {
                o.Retry.Delay = TimeSpan.FromMilliseconds(1);
                o.Retry.UseJitter = false;
            });

            services.AddOpenTelemetry()
                .WithTracing(t => t
                    // O Proxy falso roda no mesmo processo: sem este filtro
                    // os spans de servidor dele entram nas asercoes.
                    .AddAspNetCoreInstrumentation(o => o.Filter =
                        c => !c.Request.Path.StartsWithSegments("/fornecedor"))
                    .AddInMemoryExporter(Spans))
                .WithMetrics(m => m.AddInMemoryExporter(Metricas));
        });
    }

    private WebApplication CriarProxyFalso()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        // Reproduz a matriz de desfechos do Proxy real, com a mesma forma
        // de resposta, para que o mapeamento de erro seja testado de verdade.
        app.MapPost("/fornecedor/pagamentos", async (HttpContext ctx) =>
        {
            ChamadasAoProxy.Add("POST");
            var pedido = await ctx.Request.ReadFromJsonAsync<PedidoAoProxy>();

            return pedido!.Valor switch
            {
                999.99m => Results.Json(new { status = "erro", motivo = "fornecedor_timeout", detalhe = "..." }, statusCode: 504),
                999.98m => Results.Json(new { status = "erro", motivo = "fornecedor_indisponivel", detalhe = "..." }, statusCode: 502),
                >= 1000m => Results.Json(new { status = "recusado", motivo = "saldo_insuficiente", detalhe = "..." }, statusCode: 422),
                _ => Results.Ok(new { pagamentoId = PagamentoConhecido, status = "aprovado", autorizacao = "AUT-12345" }),
            };
        });

        app.MapGet("/fornecedor/pagamentos/{id:guid}", (Guid id) =>
        {
            ChamadasAoProxy.Add("GET");
            return id == PagamentoConhecido
                ? Results.Ok(new { pagamentoId = id, status = "aprovado" })
                : Results.NotFound();
        });

        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    public static readonly Guid PagamentoConhecido = Guid.Parse("0f8a4c2e-1b9d-4f7a-8c3e-5b1d9f2a6c4e");

    private static string EnderecoDe(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

    public void Flush()
    {
        Services.GetRequiredService<TracerProvider>().ForceFlush();
        Services.GetRequiredService<MeterProvider>().ForceFlush();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _proxyFalso.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_proxyFalso).Dispose();
        }
    }

    private sealed record PedidoAoProxy(string ChavePix, decimal Valor, string Descricao);
}
