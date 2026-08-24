using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Identidade de UM binário AzCopy: versão declarada + SHA-256 EXATO do executável. Nunca a versão
/// sozinha — o runbook (§25.6) e o work order (item 5) exigem correspondência de VERSÃO **e** HASH; uma
/// versão certa com hash divergente (binário substituído/adulterado) nunca é aceita.
/// </summary>
public readonly record struct AzCopyBinaryIdentity
{
    private const int MaxVersionLength = 50;

    /// <summary>Cria a identidade do binário, validando a forma da versão.</summary>
    /// <exception cref="ArgumentException"><paramref name="version"/> vazia, com caractere de controle ou longa demais.</exception>
    public AzCopyBinaryIdentity(string version, Sha256Hash sha256)
    {
        Version = TextValue.Require(version, nameof(version), MaxVersionLength);
        Sha256 = sha256;
    }

    /// <summary>Versão declarada do binário (evidência/auditoria).</summary>
    public string Version { get; }

    /// <summary>SHA-256 do executável — a prova de integridade, não apenas a versão.</summary>
    public Sha256Hash Sha256 { get; }
}

/// <summary>
/// Catálogo (configuração, não constante embarcada — o work order item 5 exige um catálogo/CONFIGURAÇÃO,
/// pois versão/hash mudam quando a operação atualiza o binário homologado no worker) de binários AzCopy
/// homologados. AzCopy só executa quando o binário observado no worker corresponde EXATAMENTE (versão E
/// hash) a uma entrada deste catálogo — versão/hash desconhecidos ou divergentes bloqueiam fail-closed
/// (item 5, acceptance criteria 2).
/// </summary>
public sealed class AzCopyHomologationCatalog
{
    private readonly IReadOnlyList<AzCopyBinaryIdentity> _homologated;

    /// <summary>Cria o catálogo a partir das entradas homologadas configuradas operacionalmente.</summary>
    /// <exception cref="ArgumentException">Nenhuma entrada homologada foi configurada.</exception>
    public AzCopyHomologationCatalog(IReadOnlyList<AzCopyBinaryIdentity> homologated)
    {
        ArgumentNullException.ThrowIfNull(homologated);
        if (homologated.Count == 0)
        {
            throw new ArgumentException(
                "Ao menos uma versão de AzCopy homologada deve ser configurada (fail-closed: sem catálogo, nenhum upload executa).",
                nameof(homologated));
        }

        _homologated = [.. homologated];
    }

    /// <summary>
    /// Verdadeiro somente quando <paramref name="observed"/> corresponde EXATAMENTE (versão E hash) a uma
    /// entrada homologada. Nunca infere compatibilidade por versão sozinha nem por hash sozinho.
    /// </summary>
    public bool IsHomologated(AzCopyBinaryIdentity observed) =>
        _homologated.Any(entry =>
            string.Equals(entry.Version, observed.Version, StringComparison.Ordinal) && entry.Sha256 == observed.Sha256);
}
