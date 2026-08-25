using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Referência à evidência canônica de UM archive (Passo 2) no instante em que uma avaliação foi computada
/// — participa exclusivamente de <see cref="ReconciliationAssessment.ComputeSourceFingerprint"/> (nunca
/// persistida como linha própria; os deltas efetivamente calculados vivem em
/// <see cref="ArchiveReconciliationItem"/>).
/// </summary>
public sealed record ReconciliationArchiveEvidenceRef(
    TargetArchiveId Archive,
    int? BeforeVersion,
    Sha256Hash? BeforeObservationHash,
    int? AfterVersion,
    Sha256Hash? AfterObservationHash);

/// <summary>
/// Avaliação IMUTÁVEL e append-only de UMA versão de reconciliação expected-vs-observed de uma wave
/// (AB-I6-007): transforma evidências canônicas JÁ PERSISTIDAS (mapping/binding/execução/upload
/// revalidados sem drift, service result do Purview, snapshots EXO before/after) em um read model técnico,
/// determinístico e auditável — NUNCA um certificate, disposition humana/final ou conclusão de
/// wave/projeto (STOP-THE-LINE do work order).
/// <para>
/// Versionamento monotônico por (onda, plano de import job): a MESMA "impressão digital do conjunto de
/// evidências-fonte" (<see cref="SourceFingerprint"/>, item 10) converge para a MESMA versão (replay
/// idempotente); uma mudança REAL em qualquer evidência-fonte (mapping/binding/upload/execução, service
/// result, snapshots EXO) produz uma nova versão — nunca sobrescreve uma anterior.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="AssessmentHash"/>
/// a partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência (item 11); os itens
/// filhos (<see cref="PstReconciliationItem"/>/<see cref="ArchiveReconciliationItem"/>) são revalidados
/// separadamente pela camada de persistência contra <see cref="PstItemsSha256"/>/<see cref="ArchiveItemsSha256"/>.
/// </para>
/// </summary>
public sealed record ReconciliationAssessment
{
    private ReconciliationAssessment(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash sourceFingerprint,
        int pstItemCount,
        Sha256Hash pstItemsSha256,
        int archiveItemCount,
        Sha256Hash archiveItemsSha256,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc,
        Sha256Hash assessmentHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        PlannedJobName = plannedJobName;
        AssessmentVersion = assessmentVersion;
        SourceFingerprint = sourceFingerprint;
        PstItemCount = pstItemCount;
        PstItemsSha256 = pstItemsSha256;
        ArchiveItemCount = archiveItemCount;
        ArchiveItemsSha256 = archiveItemsSha256;
        Correlation = correlation;
        CreatedAtUtc = createdAtUtc;
        AssessmentHash = assessmentHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Plano de import job cujo escopo de evidência foi reconciliado.</summary>
    public PurviewImportJobName PlannedJobName { get; }

    /// <summary>Versão monotônica (1..N) desta avaliação dentro de (onda, plano).</summary>
    public int AssessmentVersion { get; }

    /// <summary>
    /// Impressão digital determinística do conjunto de evidências-fonte usadas para computar esta versão
    /// (item 10) — chave de convergência idempotente: a MESMA evidência-fonte produz a MESMA versão.
    /// </summary>
    public Sha256Hash SourceFingerprint { get; }

    /// <summary>Quantidade de itens de PST persistidos para esta versão.</summary>
    public int PstItemCount { get; }

    /// <summary>Hash agregado determinístico dos itens de PST (<see cref="ReconciliationPstItemsHash"/>) — revalidado na leitura.</summary>
    public Sha256Hash PstItemsSha256 { get; }

    /// <summary>Quantidade de itens de archive persistidos para esta versão.</summary>
    public int ArchiveItemCount { get; }

    /// <summary>Hash agregado determinístico dos itens de archive (<see cref="ReconciliationArchiveItemsHash"/>) — revalidado na leitura.</summary>
    public Sha256Hash ArchiveItemsSha256 { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Hash determinístico de TODOS os campos do header persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash AssessmentHash { get; }

    /// <summary>Cria uma nova avaliação, computando <see cref="AssessmentHash"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="assessmentVersion"/> não é positivo, ou uma contagem de itens é negativa.</exception>
    public static ReconciliationAssessment Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash sourceFingerprint,
        int pstItemCount,
        Sha256Hash pstItemsSha256,
        int archiveItemCount,
        Sha256Hash archiveItemsSha256,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc)
    {
        if (assessmentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assessmentVersion), assessmentVersion, "A versão da avaliação deve ser positiva.");
        }

        if (pstItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pstItemCount), pstItemCount, "PstItemCount não pode ser negativo.");
        }

        if (archiveItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveItemCount), archiveItemCount, "ArchiveItemCount não pode ser negativo.");
        }

        var canonicalCreatedAt = TruncateToMilliseconds(createdAtUtc);
        var hash = ComputeAssessmentHash(
            tenant, project, wave, plannedJobName, assessmentVersion, sourceFingerprint, pstItemCount, pstItemsSha256,
            archiveItemCount, archiveItemsSha256, correlation, canonicalCreatedAt);

        return new ReconciliationAssessment(
            tenant, project, wave, plannedJobName, assessmentVersion, sourceFingerprint, pstItemCount, pstItemsSha256,
            archiveItemCount, archiveItemsSha256, correlation, canonicalCreatedAt, hash);
    }

    /// <summary>
    /// Reconstrói uma versão JÁ PERSISTIDA (uso exclusivo da camada de persistência), revalidando
    /// <see cref="AssessmentHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="ReconciliationIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static ReconciliationAssessment Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash sourceFingerprint,
        int pstItemCount,
        Sha256Hash pstItemsSha256,
        int archiveItemCount,
        Sha256Hash archiveItemsSha256,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc,
        Sha256Hash persistedAssessmentHash)
    {
        var recomputed = ComputeAssessmentHash(
            tenant, project, wave, plannedJobName, assessmentVersion, sourceFingerprint, pstItemCount, pstItemsSha256,
            archiveItemCount, archiveItemsSha256, correlation, createdAtUtc);
        if (!string.Equals(recomputed.Value, persistedAssessmentHash.Value, StringComparison.Ordinal))
        {
            throw new ReconciliationIntegrityViolationException(
                $"O assessment_hash persistido para a versão {assessmentVersion.ToString(CultureInfo.InvariantCulture)} da " +
                $"reconciliação do plano {plannedJobName.Value} não corresponde ao hash recomputado a partir dos campos " +
                "carregados — evidência possivelmente adulterada ou corrompida.");
        }

        return new ReconciliationAssessment(
            tenant, project, wave, plannedJobName, assessmentVersion, sourceFingerprint, pstItemCount, pstItemsSha256,
            archiveItemCount, archiveItemsSha256, correlation, createdAtUtc, persistedAssessmentHash);
    }

    /// <summary>
    /// Impressão digital determinística do conjunto de evidências-fonte (item 10), exposta para que a
    /// camada de persistência possa resolver convergência idempotente ANTES de conhecer a versão a alocar
    /// (mesmo padrão de <c>ExoArchiveStatisticsSnapshot.ComputeObservationHash</c>). Cobre a impressão
    /// digital do mapping/vínculo/execução/upload (já revalidada sem drift pelo chamador), a versão/hash da
    /// evidência do service result (quando existente) e a versão/hash de cada snapshot EXO before/after por
    /// archive — NUNCA a versão/timestamp da própria avaliação.
    /// </summary>
    public static Sha256Hash ComputeSourceFingerprint(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        Sha256Hash mappingFingerprint,
        int? reportVersion,
        Sha256Hash? reportContentSha256,
        IReadOnlyList<ReconciliationArchiveEvidenceRef> archiveEvidence)
    {
        ArgumentNullException.ThrowIfNull(archiveEvidence);

        var parts = new List<string>
        {
            "archivebridge.purview.reconciliation-source-fingerprint.v1",
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            mappingFingerprint.Value,
            reportVersion?.ToString(CultureInfo.InvariantCulture) ?? "null",
            reportContentSha256?.Value ?? "null",
            archiveEvidence.Count.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var evidence in archiveEvidence.OrderBy(item => item.Archive.Value, StringComparer.Ordinal))
        {
            parts.Add(evidence.Archive.Value);
            parts.Add(evidence.BeforeVersion?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(evidence.BeforeObservationHash?.Value ?? "null");
            parts.Add(evidence.AfterVersion?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(evidence.AfterObservationHash?.Value ?? "null");
        }

        return DeterministicHash.Compute(parts);
    }

    private static Sha256Hash ComputeAssessmentHash(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash sourceFingerprint,
        int pstItemCount,
        Sha256Hash pstItemsSha256,
        int archiveItemCount,
        Sha256Hash archiveItemsSha256,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc) =>
        DeterministicHash.Compute(
        [
            nameof(ReconciliationAssessment),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            assessmentVersion.ToString(CultureInfo.InvariantCulture),
            sourceFingerprint.Value,
            pstItemCount.ToString(CultureInfo.InvariantCulture),
            pstItemsSha256.Value,
            archiveItemCount.ToString(CultureInfo.InvariantCulture),
            archiveItemsSha256.Value,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(createdAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
