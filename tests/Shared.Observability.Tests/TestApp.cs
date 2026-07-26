using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Shared.Observability.Tests;

/// <summary>
/// Host minimo que exercita o contrato de correlacao ponta a ponta:
/// span de negocio, log e chamada de saida dentro de uma unica requisicao.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string SourceName = "Teste.Correlacao";
    public const string Rota = "/exercita";

    private static readonly ActivitySource Source = new(SourceName);

    private readonly WebApplication _app;
    private readonly WebApplication _eco;
    private readonly ConcurrentDictionary<string, string> _headersDeSaida = new();

    public List<Activity> Spans { get; } = [];
    public List<LogRecord> Logs { get; } = [];
    public HttpClient Client { get; }

    public IReadOnlyDictionary<string, string> HeadersDeSaida => _headersDeSaida;

    private TestApp()
    {
        _eco = CriarServidorDeEco();
        var enderecoDoEco = EnderecoDe(_eco);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.AddInMemoryExporter(Logs);
        });

        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t
                // A instrumentacao do ASP.NET Core escuta o DiagnosticSource do
                // processo inteiro, nao de um host. Sem este filtro, o span do
                // servidor de eco (que e andaime e nao tem AddCorrelation)
                // entraria nas asercoes.
                .AddAspNetCoreInstrumentation(o => o.Filter =
                    context => !context.Request.Path.StartsWithSegments("/recebe"))
                .AddHttpClientInstrumentation()
                .AddSource(SourceName)
                .AddInMemoryExporter(Spans));

        // A unica linha de fiacao: registra middleware, processor e handler.
        builder.AddCorrelation();

        // Sem ConfigurePrimaryHttpMessageHandler de proposito. Trocar o
        // primary handler tira o DiagnosticsHandler do caminho, e com ele
        // somem o span de cliente e a injecao de traceparent/baggage.
        builder.Services.AddHttpClient("saida", c => c.BaseAddress = new Uri(enderecoDoEco));

        _app = builder.Build();

        _app.MapGet(Rota, async (IHttpClientFactory fabrica, ILoggerFactory loggerFactory) =>
        {
            using var activity = Source.StartActivity("SpanDeNegocio");
            loggerFactory.CreateLogger("Teste").LogInformation("dentro do endpoint");
            await fabrica.CreateClient("saida").GetAsync("/recebe");
            return "ok";
        });

        _app.StartAsync().GetAwaiter().GetResult();
        Client = _app.GetTestClient();
    }

    public static TestApp Criar() => new();

    /// <summary>
    /// Servidor HTTP de verdade (Kestrel, porta dinamica). Precisa ser real
    /// para que a chamada de saida passe pela pilha HTTP completa do .NET.
    /// </summary>
    private WebApplication CriarServidorDeEco()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var eco = builder.Build();
        eco.MapGet("/recebe", (HttpContext context) =>
        {
            foreach (var header in context.Request.Headers)
                _headersDeSaida[header.Key] = header.Value.ToString();
            return Results.Ok();
        });

        eco.StartAsync().GetAwaiter().GetResult();
        return eco;
    }

    private static string EnderecoDe(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

    /// <summary>Exporters sao bufferizados; sem flush as listas vem vazias.</summary>
    public void Flush()
    {
        _app.Services.GetRequiredService<TracerProvider>().ForceFlush();
        _app.Services.GetRequiredService<LoggerProvider>().ForceFlush();
    }

    public async Task<HttpResponseMessage> ChamarAsync(string? correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Rota);
        if (correlationId is not null)
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, correlationId);

        var response = await Client.SendAsync(request);
        Flush();
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _eco.StopAsync();
        await _eco.DisposeAsync();
    }
}
