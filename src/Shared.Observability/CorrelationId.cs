namespace Shared.Observability;

/// <summary>
/// Nomes e regras do contrato de correlacao. Fixos em toda a solucao:
/// mudar qualquer um aqui quebra a correlacao entre os servicos.
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    public const string BaggageKey = "correlation.id";
    public const string TagName = "correlation.id";
    public const string LogPropertyName = "CorrelationId";

    private const int MaxLength = 64;

    /// <summary>
    /// Um id vindo de fora entra em log e em atributo de span sem redacao,
    /// entao so passa o que cabe neste alfabeto restrito.
    /// </summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static string New() => Guid.NewGuid().ToString("N");
}
