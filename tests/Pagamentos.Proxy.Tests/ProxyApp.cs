using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Pagamentos.Proxy.Tests;

internal sealed class ProxyApp : WebApplicationFactory<Program>
{
    public List<Activity> Spans { get; } = [];
    public List<Metric> Metricas { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // O cenario de latencia alta espera 3s em producao. Nos testes
        // interessa o desfecho, nao a espera.
        builder.UseSetting("Fornecedor:LatenciaAlta", "00:00:00.050");

        builder.ConfigureTestServices(services =>
        {
            services.AddOpenTelemetry()
                .WithTracing(t => t.AddInMemoryExporter(Spans))
                .WithMetrics(m => m.AddInMemoryExporter(Metricas));
        });
    }

    /// <summary>Exporters sao bufferizados; sem flush as listas vem vazias.</summary>
    public void Flush()
    {
        Services.GetRequiredService<TracerProvider>().ForceFlush();
        Services.GetRequiredService<MeterProvider>().ForceFlush();
    }

    public Activity SpanDoFornecedor() =>
        Spans.Single(s => s.DisplayName == "ChamarFornecedor");
}
