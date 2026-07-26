using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Pagamentos.Proxy.Infrastructure;

public sealed class FornecedorOptions
{
    public const string Secao = "Fornecedor";

    /// <summary>Espera do cenario de latencia alta. Reduzida nos testes.</summary>
    public TimeSpan LatenciaAlta { get; set; } = TimeSpan.FromSeconds(3);
}

public enum ResultadoFornecedor
{
    Aprovado,
    SaldoInsuficiente,
    Timeout,
    Indisponivel,
}

public sealed record RespostaFornecedor(
    ResultadoFornecedor Resultado, Guid? PagamentoId, string? Autorizacao);

/// <summary>
/// Faz o papel do banco parceiro. Os desfechos sao deterministicos por valor
/// — falha aleatoria nao serve para demonstrar tracing, porque investigar um
/// problema exige poder reproduzi-lo.
/// </summary>
internal sealed class FornecedorSimulado(IOptions<FornecedorOptions> options)
{
    public const string Nome = "banco-parceiro";

    // Criado uma vez: instrumento por chamada nao agrega.
    private static readonly Counter<long> Chamadas =
        Telemetry.Meter.CreateCounter<long>("fornecedor.chamadas");

    private readonly ConcurrentDictionary<Guid, string> _pagamentos = new();

    private const decimal LimiteDeSaldo = 1000m;
    private const decimal GatilhoTimeout = 999.99m;
    private const decimal GatilhoIndisponivel = 999.98m;
    private const decimal GatilhoLatencia = 999.97m;

    public async Task<RespostaFornecedor> ProcessarAsync(decimal valor, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.Source.StartActivity("ChamarFornecedor");
        activity?.SetTag("fornecedor.nome", Nome);

        // Os gatilhos ficam abaixo do limite de saldo, entao vem antes dele.
        var resultado = valor switch
        {
            GatilhoTimeout => ResultadoFornecedor.Timeout,
            GatilhoIndisponivel => ResultadoFornecedor.Indisponivel,
            >= LimiteDeSaldo => ResultadoFornecedor.SaldoInsuficiente,
            _ => ResultadoFornecedor.Aprovado,
        };

        if (valor == GatilhoLatencia)
            await Task.Delay(options.Value.LatenciaAlta, cancellationToken);

        activity?.SetTag("fornecedor.resultado", resultado.ToString());

        // O fornecedor e quem emite o pagamentoId, entao e ele quem guarda
        // o razao. Estado em memoria: persistencia real esta fora do escopo.
        Guid? pagamentoId = null;
        if (resultado is ResultadoFornecedor.Aprovado)
        {
            pagamentoId = Guid.NewGuid();
            _pagamentos[pagamentoId.Value] = "aprovado";
        }

        // Tag de baixa cardinalidade: um enum de quatro valores. Nunca o
        // pagamento.id nem o correlation.id, que criariam uma serie
        // temporal por requisicao.
        Chamadas.Add(1, new KeyValuePair<string, object?>("resultado", resultado.ToString()));

        return new RespostaFornecedor(
            resultado,
            pagamentoId,
            resultado is ResultadoFornecedor.Aprovado ? GerarAutorizacao() : null);
    }

    public bool TentarConsultar(Guid pagamentoId, out string status) =>
        _pagamentos.TryGetValue(pagamentoId, out status!);

    private static string GerarAutorizacao() =>
        $"AUT-{Random.Shared.Next(10000, 99999)}";
}
