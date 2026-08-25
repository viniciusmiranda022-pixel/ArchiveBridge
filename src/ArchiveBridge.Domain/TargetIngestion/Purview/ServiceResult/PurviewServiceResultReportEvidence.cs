using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UMA versão do validation report / service result importado para um
/// plano de import job (AB-I6-001 itens 6/9): metadados de custódia — hash do conteúdo bruto, hash
/// agregado das linhas normalizadas (<see cref="PurviewServiceResultRowsHash"/>), tamanho, contagem de
/// linhas e responsável. O conteúdo bruto/linhas normalizadas vivem em tabelas filhas próprias
/// (persistência), nunca neste registro. Versionamento monotônico por (onda, plano): reexecução com o
/// MESMO conteúdo (mesmo <see cref="ContentSha256"/>) converge para a MESMA versão (item 10); conteúdo
/// realmente diferente produz N+1 — nunca sobrescreve uma versão anterior.
/// </summary>
public sealed record PurviewServiceResultReportEvidence
{
    private PurviewServiceResultReportEvidence(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int reportVersion,
        Sha256Hash contentSha256,
        Sha256Hash rowsSha256,
        long rawSizeBytes,
        int rowCount,
        int? declaredTotalRows,
        string uploadedBy,
        DateTimeOffset createdAtUtc,
        Sha256Hash evidenceHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        PlannedJobName = plannedJobName;
        ReportVersion = reportVersion;
        ContentSha256 = contentSha256;
        RowsSha256 = rowsSha256;
        RawSizeBytes = rawSizeBytes;
        RowCount = rowCount;
        DeclaredTotalRows = declaredTotalRows;
        UploadedBy = uploadedBy;
        CreatedAtUtc = createdAtUtc;
        EvidenceHash = evidenceHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Plano de import job ao qual este relatório se refere.</summary>
    public PurviewImportJobName PlannedJobName { get; }

    /// <summary>Versão monotônica (1..N) deste relatório dentro do plano.</summary>
    public int ReportVersion { get; }

    /// <summary>SHA-256 do conteúdo bruto do relatório (identidade de custódia/idempotência).</summary>
    public Sha256Hash ContentSha256 { get; }

    /// <summary>Hash agregado das linhas normalizadas (<see cref="PurviewServiceResultRowsHash"/>) — revalidado na leitura.</summary>
    public Sha256Hash RowsSha256 { get; }

    /// <summary>Tamanho do conteúdo bruto, em bytes.</summary>
    public long RawSizeBytes { get; }

    /// <summary>Quantidade de linhas de dados normalizadas.</summary>
    public int RowCount { get; }

    /// <summary>Contagem total autodeclarada pelo relatório (diretiva <c>#TotalRows</c>), quando presente.</summary>
    public int? DeclaredTotalRows { get; }

    /// <summary>Operador/serviço que anexou o relatório (evidência/auditoria).</summary>
    public string UploadedBy { get; }

    /// <summary>Instante em que esta versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Hash determinístico dos metadados persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash EvidenceHash { get; }

    /// <summary>Cria uma nova versão de evidência, computando o hash de integridade dos metadados.</summary>
    /// <exception cref="ArgumentException"><paramref name="uploadedBy"/> é vazio/inválido.</exception>
    public static PurviewServiceResultReportEvidence Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int reportVersion,
        Sha256Hash contentSha256,
        Sha256Hash rowsSha256,
        long rawSizeBytes,
        int rowCount,
        int? declaredTotalRows,
        string uploadedBy,
        DateTimeOffset createdAtUtc)
    {
        var author = TextValue.Require(uploadedBy, nameof(uploadedBy), 200);
        var canonicalNow = TruncateToMilliseconds(createdAtUtc);
        var hash = ComputeEvidenceHash(
            tenant, project, wave, plannedJobName, reportVersion, contentSha256, rowsSha256, rawSizeBytes, rowCount, declaredTotalRows, author, canonicalNow);
        return new PurviewServiceResultReportEvidence(
            tenant, project, wave, plannedJobName, reportVersion, contentSha256, rowsSha256, rawSizeBytes, rowCount, declaredTotalRows, author, canonicalNow, hash);
    }

    /// <summary>
    /// Reconstrói uma versão JÁ PERSISTIDA (uso exclusivo da camada de persistência), revalidando
    /// <see cref="EvidenceHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="PurviewServiceResultIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static PurviewServiceResultReportEvidence Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int reportVersion,
        Sha256Hash contentSha256,
        Sha256Hash rowsSha256,
        long rawSizeBytes,
        int rowCount,
        int? declaredTotalRows,
        string uploadedBy,
        DateTimeOffset createdAtUtc,
        Sha256Hash persistedEvidenceHash)
    {
        var recomputed = ComputeEvidenceHash(
            tenant, project, wave, plannedJobName, reportVersion, contentSha256, rowsSha256, rawSizeBytes, rowCount, declaredTotalRows, uploadedBy, createdAtUtc);
        if (!string.Equals(recomputed.Value, persistedEvidenceHash.Value, StringComparison.Ordinal))
        {
            throw new PurviewServiceResultIntegrityViolationException(
                $"O evidence_hash persistido para a versão {reportVersion} do relatório do plano {plannedJobName.Value} não " +
                "corresponde ao hash recomputado a partir dos campos carregados — evidência possivelmente adulterada ou corrompida.");
        }

        return new PurviewServiceResultReportEvidence(
            tenant, project, wave, plannedJobName, reportVersion, contentSha256, rowsSha256, rawSizeBytes, rowCount, declaredTotalRows, uploadedBy, createdAtUtc, persistedEvidenceHash);
    }

    private static Sha256Hash ComputeEvidenceHash(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int reportVersion,
        Sha256Hash contentSha256,
        Sha256Hash rowsSha256,
        long rawSizeBytes,
        int rowCount,
        int? declaredTotalRows,
        string uploadedBy,
        DateTimeOffset createdAtUtc) =>
        DeterministicHash.Compute(
        [
            nameof(PurviewServiceResultReportEvidence),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            reportVersion.ToString(CultureInfo.InvariantCulture),
            contentSha256.Value,
            rowsSha256.Value,
            rawSizeBytes.ToString(CultureInfo.InvariantCulture),
            rowCount.ToString(CultureInfo.InvariantCulture),
            declaredTotalRows?.ToString(CultureInfo.InvariantCulture) ?? "null",
            uploadedBy,
            TruncateToMilliseconds(createdAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
