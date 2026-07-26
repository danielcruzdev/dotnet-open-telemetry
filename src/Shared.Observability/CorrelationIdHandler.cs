using OpenTelemetry;

namespace Shared.Observability;

/// <summary>
/// Passo 6: grava o header explicitamente em toda chamada de saida.
/// Redundante com o header "baggage" de proposito — o X-Correlation-Id
/// visivel em cada hop aparece em qualquer captura HTTP, sem decodificar nada.
/// </summary>
internal sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = Baggage.GetBaggage(CorrelationId.BaggageKey);

        if (!string.IsNullOrEmpty(id) && !request.Headers.Contains(CorrelationId.HeaderName))
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, id);

        return base.SendAsync(request, cancellationToken);
    }
}
