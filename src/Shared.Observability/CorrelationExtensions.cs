using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace Shared.Observability;

public static class CorrelationExtensions
{
    /// <summary>
    /// Aplica o contrato de correlacao inteiro. Uma linha por servico:
    /// a correlacao so vale se os tres servicos fizerem exatamente o mesmo,
    /// entao nao ha nada aqui para cada servico escolher.
    /// </summary>
    public static TBuilder AddCorrelation<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // DelegatingHandler nao pode ser compartilhado entre clients.
        builder.Services.AddTransient<CorrelationIdHandler>();

        // Em TODO HttpClient, nao um a um: esquecer em um cliente so
        // quebraria a correlacao naquele hop e em nenhum outro.
        builder.Services.ConfigureHttpClientDefaults(
            http => http.AddHttpMessageHandler<CorrelationIdHandler>());

        // Aditivo: compoe com o WithTracing que o ServiceDefaults ja fez.
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(new CorrelationIdSpanProcessor()));

        // IStartupFilter garante que o middleware seja o primeiro do pipeline.
        // Registrar na mao em cada servico convida ao erro de coloca-lo depois
        // do roteamento, que faz o id sumir dos logs sem nenhum erro.
        builder.Services.AddTransient<IStartupFilter, CorrelationStartupFilter>();

        return builder;
    }

    private sealed class CorrelationStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.UseMiddleware<CorrelationIdMiddleware>();
                next(app);
            };
    }
}
