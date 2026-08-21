namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Código de resultado ESTRUTURADO de uma tentativa de exportação (evita controle de fluxo por mensagem
/// textual). Só aceita um dos códigos conhecidos de <see cref="EvExportResultCodes"/> (fail-closed).
/// </summary>
public readonly record struct EvExportResultCode
{
    /// <summary>Cria um código de resultado a partir de um valor conhecido; recusa desconhecidos.</summary>
    public EvExportResultCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !EvExportResultCodes.IsKnown(value))
        {
            throw new ArgumentException($"Código de resultado de exportação EV desconhecido: '{value}'.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Texto estável do código (ex.: <c>EV_EXPORT_COMPLETED</c>).</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Registro fixo dos códigos de resultado da exportação (fonte única de verdade).</summary>
public static class EvExportResultCodes
{
    /// <summary>Tentativa concluída com sucesso — manifesto canônico persistido.</summary>
    public const string ExportCompleted = "EV_EXPORT_COMPLETED";

    /// <summary>O processo do exporter falhou (exit code não zero / stderr).</summary>
    public const string ExportFailed = "EV_EXPORT_FAILED";

    /// <summary>O processo do exporter excedeu o timeout configurado.</summary>
    public const string ExportTimeout = "EV_EXPORT_TIMEOUT";

    /// <summary>O limite de captura de saída do processo foi excedido.</summary>
    public const string ExportOutputLimitExceeded = "EV_EXPORT_OUTPUT_LIMIT_EXCEEDED";

    /// <summary>Bloqueado: capability de exportação ausente/não certificada/revogada no handshake vigente.</summary>
    public const string CapabilityNotCertified = "EV_EXPORT_CAPABILITY_NOT_CERTIFIED";

    /// <summary>Bloqueado: connector revogado.</summary>
    public const string ConnectorRevoked = "EV_EXPORT_CONNECTOR_REVOKED";

    /// <summary>Bloqueado: connector/archive fora do escopo autorizado (anti-IDOR, indistinguível de inexistente).</summary>
    public const string NotFound = "EV_EXPORT_NOT_FOUND";

    /// <summary>Bloqueado: já existe exportação em andamento para o mesmo connector.</summary>
    public const string ThrottledConnector = "EV_EXPORT_THROTTLED_CONNECTOR";

    /// <summary>Bloqueado: já existe exportação em andamento para o mesmo archive.</summary>
    public const string ThrottledArchive = "EV_EXPORT_THROTTLED_ARCHIVE";

    /// <summary>O diretório de saída resolvido escapou da raiz autorizada do connector.</summary>
    public const string OutputPathEscape = "EV_EXPORT_OUTPUT_PATH_ESCAPE";

    /// <summary>Um output PST esperado está ausente na revalidação (arquivo removido).</summary>
    public const string IntegrityMissingOutput = "EV_EXPORT_INTEGRITY_MISSING_OUTPUT";

    /// <summary>O hash/tamanho de um output PST divergiu do manifesto persistido (adulteração/corrupção).</summary>
    public const string IntegrityHashMismatch = "EV_EXPORT_INTEGRITY_HASH_MISMATCH";

    /// <summary>O conjunto de outputs físicos diverge do manifesto persistido (contagem/entradas inconsistentes).</summary>
    public const string IntegrityManifestInconsistent = "EV_EXPORT_INTEGRITY_MANIFEST_INCONSISTENT";

    /// <summary>Item nativo classificado como oversized (>250 MB PST / >2 GB nativo) foi detectado.</summary>
    public const string OversizedItemDetected = "EV_EXPORT_OVERSIZED_ITEM_DETECTED";

    /// <summary>Cercamento perdido (época/dono/lease inválido).</summary>
    public const string FencedOut = "FENCED_OUT";

    private static readonly string[] AllCodes =
    [
        ExportCompleted, ExportFailed, ExportTimeout, ExportOutputLimitExceeded, CapabilityNotCertified,
        ConnectorRevoked, NotFound, ThrottledConnector, ThrottledArchive, OutputPathEscape,
        IntegrityMissingOutput, IntegrityHashMismatch, IntegrityManifestInconsistent, OversizedItemDetected, FencedOut,
    ];

    private static readonly HashSet<string> KnownSet = new(AllCodes, StringComparer.Ordinal);

    /// <summary>Todos os códigos de resultado conhecidos, em ordem estável.</summary>
    public static IReadOnlyList<string> All => AllCodes;

    /// <summary>Indica se <paramref name="value"/> é um código de resultado conhecido.</summary>
    public static bool IsKnown(string value) => KnownSet.Contains(value);
}
