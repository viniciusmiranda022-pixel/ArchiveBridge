using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Argumentos TIPADOS e VALIDADOS do comando <c>Export-EVArchive</c> (runbook §16.3, AB-4C-005 item 7):
/// <c>-ArchiveId</c>, <c>-OutputDirectory</c>, <c>-Format PST</c> (fixo), <c>-MaxThreads</c>,
/// <c>-MaxPSTSizeMB</c> e <c>-Retry</c> (sempre presente). Nenhum campo aqui é uma string livre concatenada
/// em um script: são valores TIPADOS que a fronteira de Infrastructure passa para o processo por API segura
/// (nunca interpolação de shell) — ver <c>EvExportProcessArgumentBuilder</c>. O diretório de saída é sempre
/// o <see cref="OutputDirectoryToken"/> — um caminho RELATIVO determinístico derivado exclusivamente de IDs
/// opacos do servidor (nunca de <see cref="ExternalArchiveId"/> ou de qualquer valor fornecido pelo
/// cliente); a resolução para um caminho físico absoluto sob a raiz autorizada do connector, com contenção
/// anti-traversal/anti-reparse, acontece em Infrastructure.
/// </summary>
public sealed record EvExportCommandArguments
{
    private const int ExternalArchiveIdMaxLength = 300;
    private const int OutputDirectoryTokenMaxLength = 400;

    /// <summary>Formato fixo exigido pelo produto — a única forma autorizada nesta fatia.</summary>
    public const string FixedFormat = "PST";

    private EvExportCommandArguments(string externalArchiveId, string outputDirectoryToken, ExportRequestPolicy policy)
    {
        ExternalArchiveId = externalArchiveId;
        OutputDirectoryToken = outputDirectoryToken;
        Policy = policy;
    }

    /// <summary>Constrói os argumentos validados, aplicando a mesma sanitização de forma usada em todo o Domain EV.</summary>
    public static EvExportCommandArguments Create(string externalArchiveId, string outputDirectoryToken, ExportRequestPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new EvExportCommandArguments(
            TextValue.Require(externalArchiveId, nameof(externalArchiveId), ExternalArchiveIdMaxLength),
            TextValue.Require(outputDirectoryToken, nameof(outputDirectoryToken), OutputDirectoryTokenMaxLength),
            policy);
    }

    /// <summary>Identidade externa opaca do archive-alvo (<c>-ArchiveId</c>).</summary>
    public string ExternalArchiveId { get; }

    /// <summary>Token RELATIVO determinístico do diretório de saída (<c>-OutputDirectory</c>, resolvido em Infrastructure).</summary>
    public string OutputDirectoryToken { get; }

    /// <summary>Política validada (<c>-MaxThreads</c>/<c>-MaxPSTSizeMB</c>).</summary>
    public ExportRequestPolicy Policy { get; }

    /// <summary><c>-Format</c> — sempre PST nesta fatia.</summary>
    public string Format { get; } = FixedFormat;

    /// <summary><c>-Retry</c> — sempre presente (o cmdlet reexecuta itens transitoriamente falhos internamente).</summary>
    public bool Retry { get; } = true;
}
