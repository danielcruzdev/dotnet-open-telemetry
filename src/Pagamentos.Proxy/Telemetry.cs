using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pagamentos.Proxy;

/// <summary>
/// Um ActivitySource e um Meter por servico, de vida longa. O nome vem do
/// assembly em vez de uma constante escrita a mao: e o mesmo valor que o
/// ServiceDefaults registra via ApplicationName, e assim os dois nao podem
/// divergir. Source nao registrado nao gera span e nao avisa.
/// </summary>
internal static class Telemetry
{
    public static readonly string Name = typeof(Telemetry).Assembly.GetName().Name!;

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);
}
