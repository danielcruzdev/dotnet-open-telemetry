using Shared.Observability;

namespace Shared.Observability.Tests;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("teste-123")]
    [InlineData("abc")]
    [InlineData("A1._-")]
    [InlineData("0f8a4c2e1b9d4f7a8c3e5b1d9f2a6c4e")]
    public void IsValid_aceita_id_bem_formado(string value) =>
        Assert.True(CorrelationId.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValid_rejeita_vazio(string? value) =>
        Assert.False(CorrelationId.IsValid(value));

    [Theory]
    [InlineData("com espaco")]
    [InlineData("\"; DROP TABLE")]
    [InlineData("quebra\nde-linha")]
    [InlineData("acentuacao-ç")]
    [InlineData("barra/invertida")]
    public void IsValid_rejeita_caractere_fora_do_alfabeto_permitido(string value) =>
        Assert.False(CorrelationId.IsValid(value));

    [Fact]
    public void IsValid_aceita_exatamente_64_caracteres() =>
        Assert.True(CorrelationId.IsValid(new string('a', 64)));

    [Fact]
    public void IsValid_rejeita_acima_de_64_caracteres() =>
        Assert.False(CorrelationId.IsValid(new string('a', 65)));

    [Fact]
    public void New_gera_id_valido() =>
        Assert.True(CorrelationId.IsValid(CorrelationId.New()));

    [Fact]
    public void New_gera_ids_distintos() =>
        Assert.NotEqual(CorrelationId.New(), CorrelationId.New());
}
