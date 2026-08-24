using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>Identidade de um registro de capability evidence, gerada pelo servidor.</summary>
public readonly record struct CapabilityEvidenceId(Guid Value)
{
    /// <summary>Gera uma nova identidade.</summary>
    public static CapabilityEvidenceId New() => new(Guid.NewGuid());
}

/// <summary>
/// Registro IMUTÁVEL e versionado de capability evidence para UMA rota, escopado a tenant/projeto/provedor
/// (work order AB-I5-001 item 3). Cada descoberta é um novo registro append-only; o vigente é sempre o mais
/// recente por <see cref="Version"/>. A persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de
/// <c>EvWatermark</c>/<c>InventorySnapshot</c>): <see cref="Rehydrate"/> recomputa <see cref="EvidenceHash"/>
/// a partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência (adulteração/
/// corrupção nunca é reidratada como evidência canônica).
/// </summary>
public sealed record CapabilityEvidence
{
    private const int SourceReferenceMaxLength = 400;
    private const int DocumentationVersionMaxLength = 100;
    private const int CapabilityVersionLabelMaxLength = 100;

    private CapabilityEvidence(
        CapabilityEvidenceId id,
        TenantId tenant,
        ProjectId project,
        TargetProvider provider,
        PurviewCapabilityRoute route,
        int version,
        CapabilityStatus status,
        string? sourceReference,
        string? documentationVersion,
        string? capabilityVersionLabel,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        Sha256Hash evidenceHash)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Provider = provider;
        Route = route;
        Version = version;
        Status = status;
        SourceReference = sourceReference;
        DocumentationVersion = documentationVersion;
        CapabilityVersionLabel = capabilityVersionLabel;
        ObservedAtUtc = observedAtUtc;
        Correlation = correlation;
        RecordedAtUtc = recordedAtUtc;
        EvidenceHash = evidenceHash;
    }

    /// <summary>Registra uma nova evidência (descoberta fresca) — computa o hash de adulteração.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> não é positivo.</exception>
    public static CapabilityEvidence Record(
        CapabilityEvidenceId id,
        TenantId tenant,
        ProjectId project,
        TargetProvider provider,
        PurviewCapabilityRoute route,
        int version,
        CapabilityStatus status,
        string? sourceReference,
        string? documentationVersion,
        string? capabilityVersionLabel,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A versão da evidência deve ser positiva.");
        }

        var sanitizedSource = SanitizeOptional(sourceReference, nameof(sourceReference), SourceReferenceMaxLength);
        var sanitizedDocVersion = SanitizeOptional(documentationVersion, nameof(documentationVersion), DocumentationVersionMaxLength);
        var sanitizedCapabilityVersion = SanitizeOptional(capabilityVersionLabel, nameof(capabilityVersionLabel), CapabilityVersionLabelMaxLength);
        var canonicalObservedAtUtc = TruncateToMilliseconds(observedAtUtc);
        var canonicalRecordedAtUtc = TruncateToMilliseconds(recordedAtUtc);

        var hash = ComputeEvidenceHash(
            tenant, project, provider, route, version, status, sanitizedSource, sanitizedDocVersion,
            sanitizedCapabilityVersion, canonicalObservedAtUtc, correlation, canonicalRecordedAtUtc);

        return new CapabilityEvidence(
            id, tenant, project, provider, route, version, status, sanitizedSource, sanitizedDocVersion,
            sanitizedCapabilityVersion, canonicalObservedAtUtc, correlation, canonicalRecordedAtUtc, hash);
    }

    /// <summary>
    /// Reconstrói uma evidência já persistida, revalidando <see cref="EvidenceHash"/> contra os campos
    /// REALMENTE carregados (fail-closed). Nunca aceita uma linha adulterada/corrompida como canônica.
    /// </summary>
    /// <exception cref="CapabilityEvidenceIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static CapabilityEvidence Rehydrate(
        CapabilityEvidenceId id,
        TenantId tenant,
        ProjectId project,
        TargetProvider provider,
        PurviewCapabilityRoute route,
        int version,
        CapabilityStatus status,
        string? sourceReference,
        string? documentationVersion,
        string? capabilityVersionLabel,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        Sha256Hash persistedEvidenceHash)
    {
        var recomputed = ComputeEvidenceHash(
            tenant, project, provider, route, version, status, sourceReference, documentationVersion,
            capabilityVersionLabel, observedAtUtc, correlation, recordedAtUtc);
        if (!string.Equals(recomputed.Value, persistedEvidenceHash.Value, StringComparison.Ordinal))
        {
            throw new CapabilityEvidenceIntegrityViolationException(
                $"O evidence_hash persistido para {id.Value} não corresponde ao hash recomputado a partir dos " +
                "campos carregados — evidência possivelmente adulterada ou corrompida.");
        }

        return new CapabilityEvidence(
            id, tenant, project, provider, route, version, status, sourceReference, documentationVersion,
            capabilityVersionLabel, observedAtUtc, correlation, recordedAtUtc, persistedEvidenceHash);
    }

    /// <summary>
    /// Verdadeiro quando o CONTEÚDO lógico (rota/status/fonte/versões/data observada) é idêntico ao de
    /// <paramref name="other"/> — usado SOMENTE para detectar réplay idempotente (work order item 11); nunca
    /// inclui <see cref="RecordedAtUtc"/>/<see cref="Version"/>/<see cref="Id"/>/<see cref="Correlation"/>,
    /// que mudam a cada submissão real mesmo quando o fato documentado não mudou.
    /// </summary>
    public bool IsSameContentAs(CapabilityEvidence other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Tenant.Equals(other.Tenant)
            && Project.Equals(other.Project)
            && Provider == other.Provider
            && string.Equals(Route.Value, other.Route.Value, StringComparison.Ordinal)
            && Status == other.Status
            && string.Equals(SourceReference, other.SourceReference, StringComparison.Ordinal)
            && string.Equals(DocumentationVersion, other.DocumentationVersion, StringComparison.Ordinal)
            && string.Equals(CapabilityVersionLabel, other.CapabilityVersionLabel, StringComparison.Ordinal)
            && ObservedAtUtc == other.ObservedAtUtc;
    }

    /// <summary>Identidade do registro.</summary>
    public CapabilityEvidenceId Id { get; }

    /// <summary>Tenant do escopo.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo.</summary>
    public ProjectId Project { get; }

    /// <summary>Provedor de destino (Purview).</summary>
    public TargetProvider Provider { get; }

    /// <summary>Rota de capability descrita por este registro.</summary>
    public PurviewCapabilityRoute Route { get; }

    /// <summary>Versão monotônica crescente por (tenant, projeto, provedor, rota).</summary>
    public int Version { get; }

    /// <summary>Nível de suporte documentado.</summary>
    public CapabilityStatus Status { get; }

    /// <summary>Fonte oficial da evidência (URL/ADR/documentação) — <see langword="null"/> quando <see cref="Status"/> é <see cref="CapabilityStatus.Unknown"/>.</summary>
    public string? SourceReference { get; }

    /// <summary>Versão da documentação do fornecedor, quando disponível.</summary>
    public string? DocumentationVersion { get; }

    /// <summary>Rótulo de versão da capability, quando disponível.</summary>
    public string? CapabilityVersionLabel { get; }

    /// <summary>Data em que o fato documentado foi observado/estabelecido (não muda a cada redescoberta).</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTE registro foi persistido (usado para avaliar staleness — avança a cada redescoberta).</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash EvidenceHash { get; }

    private static string? SanitizeOptional(string? value, string parameterName, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : TextValue.Require(value, parameterName, maxLength);

    private static Sha256Hash ComputeEvidenceHash(
        TenantId tenant, ProjectId project, TargetProvider provider, PurviewCapabilityRoute route, int version,
        CapabilityStatus status, string? sourceReference, string? documentationVersion, string? capabilityVersionLabel,
        DateTimeOffset observedAtUtc, CorrelationId correlation, DateTimeOffset recordedAtUtc) =>
        DeterministicHash.Compute(
        [
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            provider.ToString(),
            route.Value,
            version.ToString(CultureInfo.InvariantCulture),
            status.ToString(),
            sourceReference ?? string.Empty,
            documentationVersion ?? string.Empty,
            capabilityVersionLabel ?? string.Empty,
            TruncateToMilliseconds(observedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(recordedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
