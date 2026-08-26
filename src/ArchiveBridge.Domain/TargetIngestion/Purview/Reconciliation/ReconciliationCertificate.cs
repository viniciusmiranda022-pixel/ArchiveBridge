using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Certificate IMUTÁVEL e append-only de UMA versão do resultado técnico de reconciliação de uma wave
/// (AB-I6-013, EPIC-07 Passo 5 — o último item do épico) — materializa, de forma determinística, verificável
/// offline e tamper-evident, o desfecho canônico (<see cref="ReconciliationOutcome"/>) já resolvido
/// EXCLUSIVAMENTE a partir de evidência canônica server-side (avaliação expected-vs-observed do Passo 3 e
/// dispositions humanas vigentes do Passo 4) — NUNCA marca wave/projeto <c>COMPLETED</c>, NUNCA é
/// cliente/sign-off final, NUNCA escreve em Purview/EXO/Graph/EV (STOP-THE-LINE do work order).
/// <para>
/// Versionamento monotônico por (onda, plano de import job): a MESMA "impressão digital de avaliação"
/// (<see cref="EvaluationFingerprint"/>, item 16) converge para a MESMA <see cref="CertificateVersion"/>
/// (replay idempotente); uma mudança REAL na evidência canônica (nova versão de avaliação, disposition nova/
/// alterada, ou detecção de <see cref="DuplicateRiskDetected"/>) produz uma nova versão — nunca sobrescreve
/// uma anterior (item 9/16-18: sem overwrite silencioso, histórico/supersession sempre preservado).
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="CertificateHash"/>
/// a partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência (item 12: todo
/// digest é recomputado e validado fail-closed em toda leitura/replay) — nenhum artefato/linha adulterada é
/// jamais devolvida, reidratada ou autorreparada silenciosamente.
/// </para>
/// </summary>
public sealed record ReconciliationCertificate
{
    /// <summary>Prefixo versionado do schema/engine do certificate (item 10) — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.purview.reconciliation-certificate.v1";

    private ReconciliationCertificate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int certificateVersion,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint,
        ReconciliationOutcome result,
        int totalItemCount,
        int incompleteItemCount,
        int deviationCount,
        Sha256Hash deviationsSha256,
        bool duplicateRiskDetected,
        Sha256Hash evaluationFingerprint,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion,
        Sha256Hash certificateHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        PlannedJobName = plannedJobName;
        CertificateVersion = certificateVersion;
        AssessmentVersion = assessmentVersion;
        AssessmentSourceFingerprint = assessmentSourceFingerprint;
        MappingFingerprint = mappingFingerprint;
        Result = result;
        TotalItemCount = totalItemCount;
        IncompleteItemCount = incompleteItemCount;
        DeviationCount = deviationCount;
        DeviationsSha256 = deviationsSha256;
        DuplicateRiskDetected = duplicateRiskDetected;
        EvaluationFingerprint = evaluationFingerprint;
        IssuedBy = issuedBy;
        IssuedByRole = issuedByRole;
        Correlation = correlation;
        GeneratedAtUtc = generatedAtUtc;
        SchemaVersion = schemaVersion;
        CertificateHash = certificateHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Plano de import job cuja cadeia canônica foi certificada.</summary>
    public PurviewImportJobName PlannedJobName { get; }

    /// <summary>Versão monotônica (1..N) deste certificate dentro de (onda, plano).</summary>
    public int CertificateVersion { get; }

    /// <summary>Versão da avaliação de reconciliação (Passo 3) sobre a qual este certificate foi computado.</summary>
    public int AssessmentVersion { get; }

    /// <summary><see cref="ReconciliationAssessment.SourceFingerprint"/> da avaliação usada — liga de forma tamper-evident o certificate à evidência técnica exata que ele certifica.</summary>
    public Sha256Hash AssessmentSourceFingerprint { get; }

    /// <summary>
    /// Impressão digital do mapping/root-de-destino (independente da tentativa/<see cref="PlannedJobName"/>,
    /// mesmo valor produzido por <c>PurviewImportJobEvidenceGuard.ResolveAndVerifyNoDriftAsync</c>) — usada
    /// exclusivamente para detectar <see cref="ReconciliationOutcome.DuplicateRisk"/> entre tentativas
    /// distintas da MESMA onda (item 27: "target/root/hash diverge de execução anterior").
    /// </summary>
    public Sha256Hash MappingFingerprint { get; }

    /// <summary>Resultado canônico determinado por <see cref="ReconciliationCertificateRules.DetermineResult"/> — nunca alterado por disposition/comentário humano após emitido.</summary>
    public ReconciliationOutcome Result { get; }

    /// <summary>Quantidade total de itens (PST + archive) da avaliação certificada.</summary>
    public int TotalItemCount { get; }

    /// <summary>Quantidade de itens <see cref="ReconciliationDisposition.IncompleteEvidence"/> da avaliação certificada.</summary>
    public int IncompleteItemCount { get; }

    /// <summary>Completude de evidência derivada de <see cref="TotalItemCount"/>/<see cref="IncompleteItemCount"/> (item 4).</summary>
    public ReconciliationCertificateEvidenceCompleteness Completeness => new(TotalItemCount, IncompleteItemCount);

    /// <summary>Quantidade de entradas do resumo estruturado de desvios (itens não-Matched da avaliação).</summary>
    public int DeviationCount { get; }

    /// <summary>
    /// Hash agregado ORDEM-INDEPENDENTE (<see cref="ReconciliationCertificateDeviationsHash"/>) do resumo
    /// estruturado de desvios — cobre, para cada item não-Matched, sua disposition técnica e o
    /// <see cref="ReconciliationCertificateDeviationCode"/> derivado da decisão vigente sobre ele (item 42:
    /// "fingerprints das dispositions vigentes"). Reflete qualquer mudança de disposition desde a última
    /// versão emitida — não duplica PII/conteúdo dos itens de origem (item 11).
    /// </summary>
    public Sha256Hash DeviationsSha256 { get; }

    /// <summary>
    /// Verdadeiro quando a evidência de mapping/root desta tentativa diverge da evidência de mapping/root de
    /// uma tentativa anterior JÁ CERTIFICADA da MESMA onda — tem precedência bloqueadora sobre qualquer
    /// sucesso (item 63).
    /// </summary>
    public bool DuplicateRiskDetected { get; }

    /// <summary>
    /// Impressão digital determinística do CONJUNTO DE EVIDÊNCIA usado para computar este certificate (item
    /// 16) — chave de convergência idempotente: a MESMA avaliação + MESMAS dispositions vigentes + MESMO
    /// sinal de duplicidade produzem a MESMA versão, independentemente de quantas vezes a emissão for
    /// reenviada (replay idêntico nunca duplica efeito).
    /// </summary>
    public Sha256Hash EvaluationFingerprint { get; }

    /// <summary>Ator server-side responsável pela emissão (nunca anônimo, nunca alegado pelo payload — resolvido via <c>IAuthenticatedActorAccessor</c>).</summary>
    public string IssuedBy { get; }

    /// <summary>Papel RBAC do ator no instante da emissão (auditável independentemente do papel atual do ator).</summary>
    public string IssuedByRole { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Versão do schema/engine do certificate (item 10) — sempre <see cref="CurrentSchemaVersion"/> para certificates emitidos por esta versão do código.</summary>
    public string SchemaVersion { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos (detecta adulteração de qualquer um deles) — recomputado e validado fail-closed em toda leitura (item 12).</summary>
    public Sha256Hash CertificateHash { get; }

    /// <summary>Cria um novo certificate, computando <see cref="CertificateHash"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="certificateVersion"/>/<paramref name="assessmentVersion"/> não são positivos, ou uma contagem é negativa.</exception>
    /// <exception cref="ArgumentException"><paramref name="issuedBy"/>/<paramref name="issuedByRole"/> vazios.</exception>
    public static ReconciliationCertificate Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int certificateVersion,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint,
        ReconciliationOutcome result,
        int totalItemCount,
        int incompleteItemCount,
        int deviationCount,
        Sha256Hash deviationsSha256,
        bool duplicateRiskDetected,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc)
    {
        if (certificateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(certificateVersion), certificateVersion, "A versão do certificate deve ser positiva.");
        }

        if (assessmentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assessmentVersion), assessmentVersion, "A versão da avaliação deve ser positiva.");
        }

        if (totalItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalItemCount), totalItemCount, "TotalItemCount não pode ser negativo.");
        }

        if (incompleteItemCount < 0 || incompleteItemCount > totalItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(incompleteItemCount), incompleteItemCount, "IncompleteItemCount não pode ser negativo nem exceder TotalItemCount.");
        }

        if (deviationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviationCount), deviationCount, "DeviationCount não pode ser negativo.");
        }

        var normalizedIssuedBy = TextValue.Require(issuedBy, nameof(issuedBy), maxLength: 200);
        var normalizedIssuedByRole = TextValue.Require(issuedByRole, nameof(issuedByRole), maxLength: 50);

        var canonicalGeneratedAt = TruncateToMilliseconds(generatedAtUtc);
        var evaluationFingerprint = ComputeEvaluationFingerprint(assessmentSourceFingerprint, deviationsSha256, duplicateRiskDetected);
        var hash = ComputeCertificateHash(
            tenant, project, wave, plannedJobName, certificateVersion, assessmentVersion, assessmentSourceFingerprint,
            mappingFingerprint, result, totalItemCount, incompleteItemCount, deviationCount, deviationsSha256,
            duplicateRiskDetected, evaluationFingerprint, normalizedIssuedBy, normalizedIssuedByRole, correlation,
            canonicalGeneratedAt, CurrentSchemaVersion);

        return new ReconciliationCertificate(
            tenant, project, wave, plannedJobName, certificateVersion, assessmentVersion, assessmentSourceFingerprint,
            mappingFingerprint, result, totalItemCount, incompleteItemCount, deviationCount, deviationsSha256,
            duplicateRiskDetected, evaluationFingerprint, normalizedIssuedBy, normalizedIssuedByRole, correlation,
            canonicalGeneratedAt, CurrentSchemaVersion, hash);
    }

    /// <summary>
    /// Reconstrói um certificate JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="CertificateHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="ReconciliationCertificateIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static ReconciliationCertificate Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int certificateVersion,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint,
        ReconciliationOutcome result,
        int totalItemCount,
        int incompleteItemCount,
        int deviationCount,
        Sha256Hash deviationsSha256,
        bool duplicateRiskDetected,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion,
        Sha256Hash persistedCertificateHash)
    {
        var evaluationFingerprint = ComputeEvaluationFingerprint(assessmentSourceFingerprint, deviationsSha256, duplicateRiskDetected);
        var recomputed = ComputeCertificateHash(
            tenant, project, wave, plannedJobName, certificateVersion, assessmentVersion, assessmentSourceFingerprint,
            mappingFingerprint, result, totalItemCount, incompleteItemCount, deviationCount, deviationsSha256,
            duplicateRiskDetected, evaluationFingerprint, issuedBy, issuedByRole, correlation, generatedAtUtc, schemaVersion);

        if (!string.Equals(recomputed.Value, persistedCertificateHash.Value, StringComparison.Ordinal))
        {
            throw new ReconciliationCertificateIntegrityViolationException(
                $"O certificate_hash persistido para a versão {certificateVersion.ToString(CultureInfo.InvariantCulture)} do " +
                $"certificate de reconciliação do plano {plannedJobName.Value} não corresponde ao hash recomputado a partir " +
                "dos campos carregados — certificate possivelmente adulterado ou corrompido.");
        }

        return new ReconciliationCertificate(
            tenant, project, wave, plannedJobName, certificateVersion, assessmentVersion, assessmentSourceFingerprint,
            mappingFingerprint, result, totalItemCount, incompleteItemCount, deviationCount, deviationsSha256,
            duplicateRiskDetected, evaluationFingerprint, issuedBy, issuedByRole, correlation, generatedAtUtc,
            schemaVersion, persistedCertificateHash);
    }

    /// <summary>
    /// Impressão digital determinística do conjunto de evidência (item 16), exposta para que a camada de
    /// persistência possa resolver convergência idempotente ANTES de conhecer a versão a alocar (mesmo
    /// padrão de <see cref="ReconciliationAssessment.ComputeSourceFingerprint"/>). Cobre a avaliação-fonte, o
    /// resumo de desvios/dispositions vigentes e o sinal de duplicidade — NUNCA a versão/timestamp/ator do
    /// próprio certificate (para que uma emissão concorrente idêntica convirja para a MESMA versão).
    /// </summary>
    public static Sha256Hash ComputeEvaluationFingerprint(Sha256Hash assessmentSourceFingerprint, Sha256Hash deviationsSha256, bool duplicateRiskDetected) =>
        DeterministicHash.Compute(
        [
            "archivebridge.purview.reconciliation-certificate-evaluation-fingerprint.v1",
            assessmentSourceFingerprint.Value,
            deviationsSha256.Value,
            duplicateRiskDetected ? "1" : "0",
        ]);

    private static Sha256Hash ComputeCertificateHash(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int certificateVersion,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint,
        ReconciliationOutcome result,
        int totalItemCount,
        int incompleteItemCount,
        int deviationCount,
        Sha256Hash deviationsSha256,
        bool duplicateRiskDetected,
        Sha256Hash evaluationFingerprint,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(ReconciliationCertificate),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            certificateVersion.ToString(CultureInfo.InvariantCulture),
            assessmentVersion.ToString(CultureInfo.InvariantCulture),
            assessmentSourceFingerprint.Value,
            mappingFingerprint.Value,
            ((int)result).ToString(CultureInfo.InvariantCulture),
            totalItemCount.ToString(CultureInfo.InvariantCulture),
            incompleteItemCount.ToString(CultureInfo.InvariantCulture),
            deviationCount.ToString(CultureInfo.InvariantCulture),
            deviationsSha256.Value,
            duplicateRiskDetected ? "1" : "0",
            evaluationFingerprint.Value,
            issuedBy,
            issuedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(generatedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
