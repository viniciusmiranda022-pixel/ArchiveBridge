using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Projects;

/// <summary>
/// Configuração do projeto que <b>afeta o resultado</b> da migração (destino). É versionada e
/// possui hash determinístico; uma alteração cria uma nova versão. Não contém segredo, SAS ou token.
/// </summary>
public sealed record ProjectConfiguration(TargetTenant TargetTenant, TargetArchivePolicy TargetArchivePolicy)
{
    /// <summary>Hash determinístico da configuração (canonicalização estável).</summary>
    public Sha256Hash ComputeHash() =>
        DeterministicHash.Compute(
        [
            nameof(ProjectConfiguration),
            TargetTenant.Value,
            ((int)TargetArchivePolicy).ToString(CultureInfo.InvariantCulture),
        ]);
}
