using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Identidade determinística do plano de particionamento (runbook §20.2). A fórmula do runbook é
/// <c>SHA256(sourceSha256 + canonicalJson(partitionPolicy) + pstEngineName + pstEngineVersion +
/// plannerVersion)</c>; aqui ela é preservada e ESTENDIDA com o escopo e a identidade do que foi
/// efetivamente inspecionado (tenant, projeto, artefato, inspeção canônica) e com o nome do planner, como
/// exige o work order (§4: "artifact/canonical-inspection identity, source hash, planner name/version,
/// policy/config fingerprint"). Sem essa extensão, dois artefatos distintos de tenants distintos com o
/// mesmo conteúdo compartilhariam identidade de plano — inaceitável sob isolamento por tenant/projeto.
/// <para>
/// Determinismo: a canonicalização usa <see cref="DeterministicHash"/> (separador U+001F, que não ocorre em
/// nenhum dos componentes) e formatação de GUID/inteiro em cultura invariante. Mesmas entradas ⇒ mesmo
/// hash, em qualquer máquina e execução. O prefixo versionado impede colisão com qualquer outro hash do
/// sistema e permite evoluir a fórmula sem reaproveitar identidades antigas.
/// </para>
/// </summary>
public static class PartitionPlanIdentity
{
    private const string PlanHashPrefix = "archivebridge.pst.partition-plan.v1";
    private const string PartKeyPrefix = "archivebridge.pst.partition-part.v1";

    /// <summary>Marcador estável para um componente ausente (ex.: sem inspeção canônica) — nunca vazio.</summary>
    private const string Absent = "none";

    /// <summary>Calcula o <c>planHash</c> determinístico das ENTRADAS do planejamento.</summary>
    /// <remarks>
    /// Desfecho/motivo NÃO entram no hash: eles são função determinística das entradas, e mantê-los fora
    /// garante que o índice único de canonicidade (sobre o hash) realmente signifique "mesmas entradas".
    /// </remarks>
    public static Sha256Hash ComputePlanHash(
        TenantId tenant,
        ProjectId project,
        PartitionPlanSource source,
        PartitionPolicy policy,
        PartitionPlannerIdentity planner)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(planner);

        return DeterministicHash.Compute(
        [
            PlanHashPrefix,
            tenant.Value.ToString("D", CultureInfo.InvariantCulture),
            project.Value.ToString("D", CultureInfo.InvariantCulture),
            source.Artifact.Value.ToString("D", CultureInfo.InvariantCulture),
            source.Inspection is { } inspection
                ? inspection.Value.ToString("D", CultureInfo.InvariantCulture)
                : Absent,
            source.SourceHash.Value,
            policy.CanonicalJson,
            source.EngineName ?? Absent,
            source.EngineVersion ?? Absent,
            planner.Name,
            planner.Version,
        ]);
    }

    /// <summary>
    /// Identificador OPACO e estável de uma parte planejada, derivado do <paramref name="planHash"/> e da
    /// sequência. Nunca inclui UPN, caminho, nome de arquivo ou qualquer dado do mailbox (runbook §20.1:
    /// "nomes não incluem UPN completo; usar IDs opacos").
    /// </summary>
    public static Sha256Hash ComputePartKey(Sha256Hash planHash, int sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequência de parte começa em 1.");
        }

        return DeterministicHash.Compute(
            [PartKeyPrefix, planHash.Value, sequence.ToString(CultureInfo.InvariantCulture)]);
    }
}
