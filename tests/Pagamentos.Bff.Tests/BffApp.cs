using System.Collections.Concurrent;
using System.Diagnostics;
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
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Pagamentos.Bff.Tests;

/// <summary>
/// Sobe o BFF com um Core falso em Kestrel real, resolvido pelo mesmo
/// service discovery de producao. O Core falso guarda os headers recebidos,
/// que e como os testes provam a propagacao sem depender de spans de um
/// host que nao esta sob teste.
/// </summary>
internal sealed class BffApp : WebApplicationFactory<Program>
{
    private readonly WebApplication _coreFalso;

    public List<Activity> Spans { get; } = [];
    public List<LogRecord> Logs { get; } = [];
    public ConcurrentDictionary<string, string> HeadersRecebidos { get; } = new();
    public List<string> ChamadasAoCore { get; } = [];

    private readonly TimeSpan? _timeoutTotal;

    /// <param name="timeoutTotal">
    /// Encurta o timeout total da resiliencia para exercitar o caminho de
    /// estouro sem esperar os 30s do padrao.
    /// </param>
    public BffApp(TimeSpan? timeoutTotal = null)
    {
        _timeoutTotal = timeoutTotal;
        _coreFalso = CriarCoreFalso();
    }

    /// <summary>Valor que faz o Core falso demorar mais que o timeout curto.</summary>
    public const decimal ValorQueDemora = 111.11m;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("services:pagamentos-core:http:0", EnderecoDe(_coreFalso));

        builder.ConfigureTestServices(services =>
        {
            services.ConfigureAll<HttpStandardResilienceOptions>(o =>
            {
                o.Retry.Delay = TimeSpan.FromMilliseconds(1);
                o.Retry.UseJitter = false;

                if (_timeoutTotal is not { } total)
                    return;

                // As opcoes se validam entre si: tentativa <= total, e a
                // janela do circuit breaker >= 2x a tentativa.
                o.AttemptTimeout.Timeout = total / 2;
                o.TotalRequestTimeout.Timeout = total;
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(1);
            });

            services.AddOpenTelemetry()
                .WithTracing(t => t
                    // BFF e Core usam a MESMA rota /pagamentos, entao filtrar
                    // por caminho nao separa os dois. O TestServer responde em
                    // "localhost" sem porta; o Core falso, em 127.0.0.1:porta.
                    .AddAspNetCoreInstrumentation(o => o.Filter =
                        c => !c.Request.Host.Port.HasValue)
                    .AddInMemoryExporter(Spans));

            services.AddLogging(l => l.AddOpenTelemetry(o =>
            {
                o.IncludeScopes = true;
                o.AddInMemoryExporter(Logs);
            }));
        });
    }

    private WebApplication CriarCoreFalso()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapPost("/pagamentos", async (HttpContext ctx) =>
        {
            ChamadasAoCore.Add("POST");
            Capturar(ctx);
            var pedido = await ctx.Request.ReadFromJsonAsync<PedidoAoCore>();

            if (pedido!.Valor == ValorQueDemora)
                await Task.Delay(TimeSpan.FromSeconds(5), ctx.RequestAborted);

            return pedido.Valor switch
            {
                999.99m => Results.Json(new { status = "erro", motivo = "fornecedor_timeout", detalhe = "..." }, statusCode: 504),
                999.98m => Results.Json(new { status = "erro", motivo = "fornecedor_indisponivel", detalhe = "..." }, statusCode: 502),
                >= 1000m => Results.Json(new { status = "recusado", motivo = "saldo_insuficiente", detalhe = "..." }, statusCode: 422),
                _ when pedido.ChavePix == "recusa-chave" => Results.Json(new { status = "recusado", motivo = "chave_invalida", detalhe = "..." }, statusCode: 422),
                _ => Results.Ok(new { pagamentoId = PagamentoConhecido, status = "aprovado", autorizacao = "AUT-12345" }),
            };
        });

        app.MapGet("/pagamentos/{id:guid}", (Guid id, HttpContext ctx) =>
        {
            ChamadasAoCore.Add("GET");
            Capturar(ctx);
            return id == PagamentoConhecido
                ? Results.Ok(new { pagamentoId = id, status = "aprovado" })
                : Results.NotFound();
        });

        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    private void Capturar(HttpContext ctx)
    {
        foreach (var header in ctx.Request.Headers)
            HeadersRecebidos[header.Key] = header.Value.ToString();
    }

    public static readonly Guid PagamentoConhecido = Guid.Parse("0f8a4c2e-1b9d-4f7a-8c3e-5b1d9f2a6c4e");

    /// <summary>SpanId do pai declarado no traceparent que chegou ao Core.</summary>
    public string? SpanIdPaiRecebido =>
        HeadersRecebidos.TryGetValue("traceparent", out var tp) && tp.Split('-') is { Length: 4 } partes
            ? partes[2]
            : null;

    public string? TraceIdRecebido =>
        HeadersRecebidos.TryGetValue("traceparent", out var tp) && tp.Split('-') is { Length: 4 } partes
            ? partes[1]
            : null;

    private static string EnderecoDe(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

    public void Flush()
    {
        Services.GetRequiredService<TracerProvider>().ForceFlush();
        Services.GetRequiredService<LoggerProvider>().ForceFlush();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _coreFalso.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_coreFalso).Dispose();
        }
    }

    private sealed record PedidoAoCore(string ChavePix, decimal Valor, string Descricao);
}
