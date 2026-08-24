namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Wrapper opaco para um valor secreto em trânsito (ex.: URL SAS do Purview — AB-I5-004 item 3/13/14).
/// Por desenho: SEM <c>ToString</c> que imprima o conteúdo, SEM igualdade/hash estrutural sobre o valor
/// (evita comparações que vazariam por timing ou apareceriam em asserts de teste) e SEM nenhuma
/// propriedade pública de dados — o único ponto de acesso ao valor bruto é <see cref="Reveal"/>,
/// reservado à fronteira de custódia (o adapter de secret store que vai proteger/transmitir o segredo).
/// Um serializador automático (System.Text.Json, model binding dumps, problem details, debugger
/// display) que tente inspecionar esta instância não encontra nenhum membro público de dados — nunca o
/// segredo. Não é um record: um record geraria <c>ToString</c>/igualdade estruturais automaticamente,
/// exatamente o vetor de vazamento que este tipo existe para fechar.
/// </summary>
public sealed class RedactedSecret
{
    private const string RedactedPlaceholder = "[REDACTED]";

    private readonly string _value;

    private RedactedSecret(string value) => _value = value;

    /// <summary>Envolve um valor secreto não vazio. Fail-closed: nulo/vazio/whitespace é recusado.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> é nulo, vazio ou somente whitespace.</exception>
    public static RedactedSecret Wrap(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("O valor secreto não pode ser vazio.", nameof(value));
        }

        return new RedactedSecret(value);
    }

    /// <summary>
    /// Único ponto de acesso ao valor bruto. Usar SOMENTE na fronteira de custódia (adapter de secret
    /// store) — nunca para logging, telemetria, exceptions, audit payload ou resposta ao chamador.
    /// </summary>
    public string Reveal() => _value;

    /// <summary>Nunca imprime o valor — usado por logging/interpolação acidental.</summary>
    public override string ToString() => RedactedPlaceholder;
}
