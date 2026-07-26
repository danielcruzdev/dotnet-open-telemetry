using Microsoft.Extensions.Options;
using Pagamentos.Proxy.Infrastructure;

namespace Pagamentos.Proxy.Tests;

/// <summary>
/// A decisao do fornecedor, direto — sem HTTP no caminho. Os testes de
/// endpoint ja cobrem o mesmo pelo lado de fora, mas aqui o custo e
/// microssegundos e a falha aponta direto para a regra.
/// </summary>
public sealed class FornecedorSimuladoTests
{
    [Theory]
    [InlineData(0.01, ResultadoFornecedor.Aprovado)]
    [InlineData(150.00, ResultadoFornecedor.Aprovado)]
    [InlineData(999.96, ResultadoFornecedor.Aprovado)]
    [InlineData(999.97, ResultadoFornecedor.Aprovado)]
    [InlineData(999.98, ResultadoFornecedor.Indisponivel)]
    [InlineData(999.99, ResultadoFornecedor.Timeout)]
    [InlineData(1000.00, ResultadoFornecedor.SaldoInsuficiente)]
    [InlineData(50000.00, ResultadoFornecedor.SaldoInsuficiente)]
    public async Task Decide_pelo_valor(decimal valor, ResultadoFornecedor esperado)
    {
        var resposta = await Criar().ProcessarAsync(valor, CancellationToken.None);

        Assert.Equal(esperado, resposta.Resultado);
    }

    [Fact]
    public async Task Os_gatilhos_ficam_abaixo_do_limite_de_saldo()
    {
        var fornecedor = Criar();

        // 999.98 e 999.99 sao menores que 1000, entao a ordem das regras
        // importa: avaliar o limite de saldo primeiro engoliria os dois.
        Assert.Equal(ResultadoFornecedor.Indisponivel,
            (await fornecedor.ProcessarAsync(999.98m, CancellationToken.None)).Resultado);
        Assert.Equal(ResultadoFornecedor.Timeout,
            (await fornecedor.ProcessarAsync(999.99m, CancellationToken.None)).Resultado);
    }

    [Fact]
    public async Task Aprovado_recebe_id_e_autorizacao()
    {
        var resposta = await Criar().ProcessarAsync(150m, CancellationToken.None);

        Assert.NotNull(resposta.PagamentoId);
        Assert.NotEqual(Guid.Empty, resposta.PagamentoId!.Value);
        Assert.False(string.IsNullOrWhiteSpace(resposta.Autorizacao));
    }

    [Theory]
    [InlineData(1000.00)]
    [InlineData(999.99)]
    [InlineData(999.98)]
    public async Task Nao_aprovado_nao_gera_id_nem_autorizacao(decimal valor)
    {
        var resposta = await Criar().ProcessarAsync(valor, CancellationToken.None);

        Assert.Null(resposta.PagamentoId);
        Assert.Null(resposta.Autorizacao);
    }

    [Fact]
    public async Task Somente_aprovado_entra_no_razao()
    {
        var fornecedor = Criar();

        var aprovado = await fornecedor.ProcessarAsync(150m, CancellationToken.None);
        await fornecedor.ProcessarAsync(1000m, CancellationToken.None);

        Assert.True(fornecedor.TentarConsultar(aprovado.PagamentoId!.Value, out var status));
        Assert.Equal("aprovado", status);
        Assert.False(fornecedor.TentarConsultar(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task Gatilho_de_latencia_respeita_a_configuracao()
    {
        var fornecedor = Criar(TimeSpan.FromMilliseconds(200));

        var inicio = TimeProvider.System.GetTimestamp();
        await fornecedor.ProcessarAsync(999.97m, CancellationToken.None);
        var decorrido = TimeProvider.System.GetElapsedTime(inicio);

        Assert.True(decorrido >= TimeSpan.FromMilliseconds(150),
            $"esperava espera perceptivel, levou {decorrido.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Valor_comum_nao_espera()
    {
        var fornecedor = Criar(TimeSpan.FromSeconds(10));

        var inicio = TimeProvider.System.GetTimestamp();
        await fornecedor.ProcessarAsync(150m, CancellationToken.None);

        Assert.True(TimeProvider.System.GetElapsedTime(inicio) < TimeSpan.FromSeconds(1));
    }

    private static FornecedorSimulado Criar(TimeSpan? latencia = null) =>
        new(Options.Create(new FornecedorOptions
        {
            LatenciaAlta = latencia ?? TimeSpan.FromMilliseconds(1),
        }));
}
