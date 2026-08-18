namespace ArchiveBridge.Application.Mapping;

/// <summary>
/// Limites do backend de RECEPÇÃO de CSV de mapping. O <see cref="EffectiveMaxUploadBytes"/> (o default
/// configurável) limita a leitura do stream INDEPENDENTEMENTE de qualquer Content-Length declarado. O teto
/// absoluto <see cref="AbsoluteMaxUploadBytes"/> é uma CONSTANTE estrutural (50 MiB) que NENHUM chamador pode
/// parametrizar nem elevar: o limite efetivo é sempre validado contra ele. Também define o teto de problemas
/// de validação persistidos (a lista é truncada deterministicamente acima dele).
/// </summary>
public sealed record MappingUploadLimits
{
    private const long Mib = 1024L * 1024L;

    /// <summary>
    /// Teto ABSOLUTO e imutável de bytes de upload: 50 MiB. Não é parametrizável — é uma constante do
    /// domínio de recepção. O limite efetivo jamais pode excedê-lo (validado no construtor).
    /// </summary>
    public const long AbsoluteMaxUploadBytes = 50L * 1024L * 1024L;

    /// <summary>
    /// Cria os limites. O único grau de liberdade de tamanho é o limite efetivo, que deve satisfazer
    /// <c>0 &lt; effective &lt;= <see cref="AbsoluteMaxUploadBytes"/></c>. O teto absoluto NÃO é um parâmetro.
    /// </summary>
    public MappingUploadLimits(
        long effectiveMaxUploadBytes = 5 * Mib,
        int maxPersistedValidationIssues = 1000)
    {
        if (effectiveMaxUploadBytes <= 0 || effectiveMaxUploadBytes > AbsoluteMaxUploadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveMaxUploadBytes),
                effectiveMaxUploadBytes,
                $"O limite efetivo de upload deve ser > 0 e <= {AbsoluteMaxUploadBytes} bytes (teto absoluto).");
        }

        if (maxPersistedValidationIssues <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPersistedValidationIssues), maxPersistedValidationIssues, "O teto de problemas deve ser > 0.");
        }

        EffectiveMaxUploadBytes = effectiveMaxUploadBytes;
        MaxPersistedValidationIssues = maxPersistedValidationIssues;
    }

    /// <summary>Limite efetivo (default configurável) de bytes lidos do stream. Nunca depende do cliente.</summary>
    public long EffectiveMaxUploadBytes { get; }

    /// <summary>Teto de problemas de validação persistidos (a lista é truncada acima dele).</summary>
    public int MaxPersistedValidationIssues { get; }

    /// <summary>Limites padrão conservadores: 5 MiB efetivo (dentro do teto absoluto de 50 MiB), 1000 problemas.</summary>
    public static MappingUploadLimits Default { get; } = new();
}
