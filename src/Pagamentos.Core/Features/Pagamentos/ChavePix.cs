using System.Text.RegularExpressions;

namespace Pagamentos.Core.Features.Pagamentos;

public enum TipoChavePix
{
    Cpf,
    Cnpj,
    Email,
    Telefone,
    Aleatoria,
}

public static partial class ChavePix
{
    /// <summary>
    /// Classifica a chave sem nunca expor o valor. So o tipo vai para span e
    /// log — a chave em si e dado sensivel e fica de fora da telemetria.
    /// </summary>
    public static bool TentarClassificar(string? chave, out TipoChavePix tipo)
    {
        tipo = default;

        if (string.IsNullOrWhiteSpace(chave))
            return false;

        if (Guid.TryParseExact(chave, "D", out _))
        {
            tipo = TipoChavePix.Aleatoria;
            return true;
        }

        if (chave.Contains('@'))
        {
            if (!EmailRegex().IsMatch(chave))
                return false;

            tipo = TipoChavePix.Email;
            return true;
        }

        // O prefixo "+" e o que separa telefone de CPF: sem ele, um numero
        // de 11 digitos seria ambiguo.
        if (chave.StartsWith('+'))
        {
            if (!TelefoneRegex().IsMatch(chave))
                return false;

            tipo = TipoChavePix.Telefone;
            return true;
        }

        if (!chave.All(char.IsAsciiDigit))
            return false;

        switch (chave.Length)
        {
            case 11:
                tipo = TipoChavePix.Cpf;
                return true;
            case 14:
                tipo = TipoChavePix.Cnpj;
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\+\d{12,14}$")]
    private static partial Regex TelefoneRegex();
}
