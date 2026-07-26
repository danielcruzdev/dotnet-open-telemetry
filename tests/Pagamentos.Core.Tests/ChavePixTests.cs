using Pagamentos.Core.Features.Pagamentos;

namespace Pagamentos.Core.Tests;

public sealed class ChavePixTests
{
    [Theory]
    [InlineData("usuario@exemplo.com", TipoChavePix.Email)]
    [InlineData("a.b-c@sub.dominio.com.br", TipoChavePix.Email)]
    [InlineData("12345678901", TipoChavePix.Cpf)]
    [InlineData("12345678000199", TipoChavePix.Cnpj)]
    [InlineData("+5511987654321", TipoChavePix.Telefone)]
    [InlineData("0f8a4c2e-1b9d-4f7a-8c3e-5b1d9f2a6c4e", TipoChavePix.Aleatoria)]
    public void Classifica_chave_valida(string chave, TipoChavePix esperado)
    {
        Assert.True(ChavePix.TentarClassificar(chave, out var tipo));
        Assert.Equal(esperado, tipo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem-arroba")]
    [InlineData("123")]
    [InlineData("123456789012")]
    [InlineData("@dominio.com")]
    [InlineData("usuario@")]
    [InlineData("+55")]
    public void Rejeita_chave_invalida(string? chave) =>
        Assert.False(ChavePix.TentarClassificar(chave, out _));
}
