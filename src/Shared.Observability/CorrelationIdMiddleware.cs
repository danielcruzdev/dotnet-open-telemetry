using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Shared.Observability;

/// <summary>
/// Passos 1, 2, 4 e 5 do contrato: le ou gera o id, coloca no Baggage,
/// abre o escopo de log e devolve o header na resposta.
/// </summary>
internal sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var recebido = context.Request.Headers[CorrelationId.HeaderName].FirstOrDefault();
        var correlationId = CorrelationId.IsValid(recebido) ? recebido! : CorrelationId.New();

        Baggage.SetBaggage(CorrelationId.BaggageKey, correlationId);

        // O span do servidor ja nasceu quando este middleware roda, entao o
        // CorrelationIdSpanProcessor nao chegou a ve-lo com o Baggage
        // preenchido. Marcamos ele aqui, na mao.
        Activity.Current?.SetTag(CorrelationId.TagName, correlationId);

        // OnStarting e o unico ponto seguro: escrever no Headers depois do
        // next() lanca quando a resposta ja comecou.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationId.HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        var escopo = new Dictionary<string, object>
        {
            [CorrelationId.LogPropertyName] = correlationId,
        };

        using (logger.BeginScope(escopo))
        {
            await next(context);
        }
    }
}
