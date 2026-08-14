namespace ArchiveBridge.Application.Mapping;

/// <summary>
/// Limites do backend de RECEPÇÃO de CSV de mapping. O <see cref="EffectiveMaxUploadBytes"/> (o default
/// configurável) limita a leitura do stream INDEPENDENTEMENTE de qualquer Content-Length declarado; o
/// <see cref="HardMaxUploadBytes"/> é um teto absoluto que o default nunca pode exceder. Também define o
/// teto de problemas de validação persistidos (a lista é truncada deterministicamente acima dele).
/// </summary>
public sealed record MappingUploadLimits
{
    private const long Mib = 1024L * 1024L;

    /// <summary>Cria os limites, validando <c>0 &lt; default &lt;= hard</c> e teto de problemas &gt; 0.</summary>
    public MappingUploadLimits(
        long effectiveMaxUploadBytes = 5 * Mib,
        long hardMaxUploadBytes = 50 * Mib,
        int maxPersistedValidationIssues = 1000)
    {
        if (effectiveMaxUploadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveMaxUploadBytes), effectiveMaxUploadBytes, "O limite efetivo de upload deve ser > 0.");
        }

        if (hardMaxUploadBytes <= 0 || effectiveMaxUploadBytes > hardMaxUploadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hardMaxUploadBytes), hardMaxUploadBytes,
                "O teto absoluto deve ser > 0 e >= ao limite efetivo.");
        }

        if (maxPersistedValidationIssues <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPersistedValidationIssues), maxPersistedValidationIssues, "O teto de problemas deve ser > 0.");
        }

        EffectiveMaxUploadBytes = effectiveMaxUploadBytes;
        HardMaxUploadBytes = hardMaxUploadBytes;
        MaxPersistedValidationIssues = maxPersistedValidationIssues;
    }

    /// <summary>Limite efetivo (default configurável) de bytes lidos do stream. Nunca depende do cliente.</summary>
    public long EffectiveMaxUploadBytes { get; }

    /// <summary>Teto absoluto de bytes que o limite efetivo jamais pode exceder.</summary>
    public long HardMaxUploadBytes { get; }

    /// <summary>Teto de problemas de validação persistidos (a lista é truncada acima dele).</summary>
    public int MaxPersistedValidationIssues { get; }

    /// <summary>Limites padrão conservadores: 5 MiB efetivo, 50 MiB teto absoluto, 1000 problemas.</summary>
    public static MappingUploadLimits Default { get; } = new();
}
