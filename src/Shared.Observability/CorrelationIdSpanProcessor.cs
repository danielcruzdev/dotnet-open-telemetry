using System.Diagnostics;
using OpenTelemetry;

namespace Shared.Observability;

/// <summary>
/// Copia o correlationId do Baggage para tag de TODO span que nasce na
/// requisicao — nao so o raiz. Sem isso, o span do banco, o da chamada de
/// saida e os de negocio ficam impossiveis de achar pelo correlationId.
/// </summary>
internal sealed class CorrelationIdSpanProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity activity)
    {
        var id = Baggage.GetBaggage(CorrelationId.BaggageKey);
        if (!string.IsNullOrEmpty(id))
            activity.SetTag(CorrelationId.TagName, id);
    }
}
