using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Shared.Observability.Tests;

/// <summary>
/// O ServiceDefaults registra ActivitySource e Meter pelo ApplicationName.
/// Cada servico nomeia os seus pelo nome do proprio assembly, que e o mesmo
/// valor — estes testes fixam esse acordo. Um source nao registrado nao gera
/// span e nao levanta erro nenhum.
/// </summary>
public sealed class ServiceDefaultsTests
{
    [Fact]
    public async Task ActivitySource_com_o_nome_da_aplicacao_e_registrado()
    {
        List<Activity> spans = [];
        await using var app = CriarApp(
            tracing => tracing.AddInMemoryExporter(spans),
            metrics => { });

        using (var source = new ActivitySource(NomeDaAplicacao(app)))
        using (source.StartActivity("SpanDeProva")) { }

        app.Services.GetRequiredService<TracerProvider>().ForceFlush();

        Assert.Contains(spans, s => s.DisplayName == "SpanDeProva");
    }

    [Fact]
    public async Task Meter_com_o_nome_da_aplicacao_e_registrado()
    {
        List<Metric> metrics = [];
        await using var app = CriarApp(
            tracing => { },
            m => m.AddInMemoryExporter(metrics));

        using var meter = new Meter(NomeDaAplicacao(app));
        meter.CreateCounter<long>("prova.contador").Add(1);

        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Contains(metrics, m => m.Name == "prova.contador");
    }

    [Fact]
    public async Task ServiceDefaults_habilita_IncludeScopes()
    {
        List<LogRecord> logs = [];
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // AddServiceDefaults é a única fonte de configuração de log aqui.
        // Os outros projetos de teste montam o próprio logging e por isso
        // nao protegem este valor: se alguem desligar IncludeScopes no
        // ServiceDefaults, os tres servicos perdem o CorrelationId dos logs
        // em producao sem nenhum teste reclamar.
        builder.AddServiceDefaults();
        builder.Logging.AddOpenTelemetry(o => o.AddInMemoryExporter(logs));

        await using var app = builder.Build();
        await app.StartAsync();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Teste");
        using (logger.BeginScope(new Dictionary<string, object> { ["Prova"] = "valor" }))
        {
            logger.LogInformation("dentro do escopo");
        }

        app.Services.GetRequiredService<LoggerProvider>().ForceFlush();

        var registro = logs.Single(l => l.Body == "dentro do escopo");
        var achou = false;
        registro.ForEachScope(
            (escopo, _) =>
            {
                foreach (var item in escopo)
                    if (item.Key == "Prova")
                        achou = true;
            },
            default(object));

        Assert.True(achou, "o escopo nao chegou ao LogRecord: IncludeScopes esta desligado");
    }

    private static string NomeDaAplicacao(WebApplication app) => app.Environment.ApplicationName;

    private static WebApplication CriarApp(
        Action<TracerProviderBuilder> tracing,
        Action<MeterProviderBuilder> metrics)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.AddServiceDefaults();
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing)
            .WithMetrics(metrics);

        var app = builder.Build();
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }
}
