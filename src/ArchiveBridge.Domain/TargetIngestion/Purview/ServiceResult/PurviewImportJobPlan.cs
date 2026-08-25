using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UMA tentativa de planejamento do import job do Purview de uma onda
/// (AB-I6-001 item 4) — o nome planejado é vinculado à onda ANTES de qualquer observação de provider ser
/// aceita. <see cref="EvidenceFingerprint"/> reaproveita
/// <see cref="TargetIngestion.Purview.MappingCsv.PurviewMappingGenerationFingerprint"/> (a MESMA
/// impressão digital que rege a idempotência/drift do mapping CSV publicado — item 8 "reuse where it
/// already satisfies"): duas tentativas de planejamento só convergem (mesmo <see cref="AttemptSequence"/>)
/// quando a evidência canônica de vínculo/execução/upload/mailbox é EXATAMENTE a mesma; qualquer mudança
/// real produz uma nova tentativa/nome. Nunca contém o conteúdo do mapping nem qualquer segredo/PII.
/// </summary>
public sealed record PurviewImportJobPlan
{
    private PurviewImportJobPlan(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int attemptSequence,
        PurviewImportJobName plannedJobName,
        PurviewMappingGenerationFingerprint evidenceFingerprint,
        string createdBy,
        DateTimeOffset createdAtUtc,
        Sha256Hash planHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        AttemptSequence = attemptSequence;
        PlannedJobName = plannedJobName;
        EvidenceFingerprint = evidenceFingerprint;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
        PlanHash = planHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Sequência de tentativa (1..N) desta onda — cresce monotonicamente, nunca reaproveitada.</summary>
    public int AttemptSequence { get; }

    /// <summary>Nome planejado, determinístico e server-side (<see cref="PurviewImportJobName"/>).</summary>
    public PurviewImportJobName PlannedJobName { get; }

    /// <summary>
    /// Impressão digital da evidência canônica (vínculos/execuções/upload verificado/mailbox) no
    /// instante do planejamento — usada para detectar drift antes de aceitar observação/resultado.
    /// </summary>
    public PurviewMappingGenerationFingerprint EvidenceFingerprint { get; }

    /// <summary>Operador/serviço que solicitou o planejamento (evidência/auditoria).</summary>
    public string CreatedBy { get; }

    /// <summary>Instante em que o plano foi criado (append-only — nunca mutado depois).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Hash determinístico de todos os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash PlanHash { get; }

    /// <summary>Cria um novo plano, computando o hash de integridade.</summary>
    /// <exception cref="ArgumentException"><paramref name="createdBy"/> é vazio/inválido.</exception>
    public static PurviewImportJobPlan Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int attemptSequence,
        PurviewImportJobName plannedJobName,
        PurviewMappingGenerationFingerprint evidenceFingerprint,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        var author = TextValue.Require(createdBy, nameof(createdBy), 200);
        var canonicalNow = TruncateToMilliseconds(createdAtUtc);
        var hash = ComputePlanHash(tenant, project, wave, attemptSequence, plannedJobName, evidenceFingerprint, author, canonicalNow);
        return new PurviewImportJobPlan(tenant, project, wave, attemptSequence, plannedJobName, evidenceFingerprint, author, canonicalNow, hash);
    }

    /// <summary>
    /// Reconstrói um plano JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="PlanHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="PurviewImportJobIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static PurviewImportJobPlan Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int attemptSequence,
        PurviewImportJobName plannedJobName,
        PurviewMappingGenerationFingerprint evidenceFingerprint,
        string createdBy,
        DateTimeOffset createdAtUtc,
        Sha256Hash persistedPlanHash)
    {
        var recomputed = ComputePlanHash(tenant, project, wave, attemptSequence, plannedJobName, evidenceFingerprint, createdBy, createdAtUtc);
        if (!string.Equals(recomputed.Value, persistedPlanHash.Value, StringComparison.Ordinal))
        {
            throw new PurviewImportJobIntegrityViolationException(
                $"O plan_hash persistido para a tentativa {attemptSequence} da onda {wave.Value} não corresponde ao hash " +
                "recomputado a partir dos campos carregados — plano possivelmente adulterado ou corrompido.");
        }

        return new PurviewImportJobPlan(tenant, project, wave, attemptSequence, plannedJobName, evidenceFingerprint, createdBy, createdAtUtc, persistedPlanHash);
    }

    private static Sha256Hash ComputePlanHash(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int attemptSequence,
        PurviewImportJobName plannedJobName,
        PurviewMappingGenerationFingerprint evidenceFingerprint,
        string createdBy,
        DateTimeOffset createdAtUtc) =>
        DeterministicHash.Compute(
        [
            nameof(PurviewImportJobPlan),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            attemptSequence.ToString(CultureInfo.InvariantCulture),
            plannedJobName.Value,
            evidenceFingerprint.Value.Value,
            createdBy,
            TruncateToMilliseconds(createdAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
