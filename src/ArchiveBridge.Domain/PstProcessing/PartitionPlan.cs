using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>Identidade de um plano de particionamento, gerada pelo servidor.</summary>
public readonly record struct PartitionPlanId(Guid Value)
{
    /// <summary>Gera uma nova identidade de plano.</summary>
    public static PartitionPlanId New() => new(Guid.NewGuid());
}

/// <summary>Identidade de uma parte planejada, gerada pelo servidor.</summary>
public readonly record struct PartitionPlanPartId(Guid Value)
{
    /// <summary>Gera uma nova identidade de parte.</summary>
    public static PartitionPlanPartId New() => new(Guid.NewGuid());
}

/// <summary>Nome/versão do planner que produziu o plano (participa da identidade determinística).</summary>
public sealed record PartitionPlannerIdentity
{
    /// <summary>Cria a identidade do planner, validando forma e tamanho.</summary>
    /// <exception cref="ArgumentException">Nome/versão vazios, com caractere de controle ou longos demais.</exception>
    public PartitionPlannerIdentity(string name, string version)
    {
        Name = TextValue.Require(name, nameof(name), 100);
        Version = TextValue.Require(version, nameof(version), 50);
    }

    /// <summary>Nome estável do planner (evidência/auditoria).</summary>
    public string Name { get; }

    /// <summary>Versão do planner (evidência/auditoria).</summary>
    public string Version { get; }
}

/// <summary>
/// Origem do planejamento: o artefato de custódia, a inspeção canônica que o descreve (quando existe) e o
/// que ela observou. <see cref="Inspection"/>/<see cref="SourceSizeBytes"/>/<see cref="EngineName"/>/
/// <see cref="EngineVersion"/> são nulos APENAS quando não há inspeção canônica utilizável — o plano
/// resultante nunca pode ser <see cref="PartitionPlanOutcome.Planned"/> nesse caso.
/// </summary>
public sealed record PartitionPlanSource
{
    /// <summary>Cria a origem do planejamento.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Tamanho observado negativo.</exception>
    public PartitionPlanSource(
        ArtifactId artifact,
        InspectionId? inspection,
        Sha256Hash sourceHash,
        long? sourceSizeBytes,
        string? engineName,
        string? engineVersion)
    {
        if (sourceSizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSizeBytes), "Tamanho de origem não pode ser negativo.");
        }

        Artifact = artifact;
        Inspection = inspection;
        SourceHash = sourceHash;
        SourceSizeBytes = sourceSizeBytes;
        EngineName = engineName is null ? null : TextValue.Require(engineName, nameof(engineName), 100);
        EngineVersion = engineVersion is null ? null : TextValue.Require(engineVersion, nameof(engineVersion), 50);
    }

    /// <summary>Artefato sob custódia que seria particionado.</summary>
    public ArtifactId Artifact { get; }

    /// <summary>Inspeção canônica que originou o plano (nula quando não há inspeção utilizável).</summary>
    public InspectionId? Inspection { get; }

    /// <summary>Hash SHA-256 da origem, registrado em custódia (baseline de coerência).</summary>
    public Sha256Hash SourceHash { get; }

    /// <summary>Tamanho observado na inspeção canônica, em bytes.</summary>
    public long? SourceSizeBytes { get; }

    /// <summary>Nome da engine que produziu a inspeção canônica (runbook §20.2: <c>pstEngineName</c>).</summary>
    public string? EngineName { get; }

    /// <summary>Versão da engine que produziu a inspeção canônica (runbook §20.2: <c>pstEngineVersion</c>).</summary>
    public string? EngineVersion { get; }
}

/// <summary>
/// Uma parte planejada. É INTENÇÃO, não execução: nenhum PST de saída existe, nenhum byte é copiado.
/// <see cref="PartKey"/> é opaco (derivado do <c>planHash</c> + sequência) e nunca carrega UPN/caminho.
/// </summary>
public sealed record PartitionPlanPart
{
    /// <summary>Cria uma parte planejada.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Sequência &lt; 1 ou tamanho negativo.</exception>
    public PartitionPlanPart(
        PartitionPlanPartId id, int sequence, Sha256Hash partKey, long plannedSizeBytes, bool coversEntireSource)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequência de parte começa em 1.");
        }

        if (plannedSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plannedSizeBytes), "Tamanho planejado não pode ser negativo.");
        }

        Id = id;
        Sequence = sequence;
        PartKey = partKey;
        PlannedSizeBytes = plannedSizeBytes;
        CoversEntireSource = coversEntireSource;
    }

    /// <summary>Identidade da parte.</summary>
    public PartitionPlanPartId Id { get; }

    /// <summary>Sequência estável e contígua dentro do plano (1..N).</summary>
    public int Sequence { get; }

    /// <summary>Chave opaca determinística da parte.</summary>
    public Sha256Hash PartKey { get; }

    /// <summary>Tamanho planejado da parte, em bytes.</summary>
    public long PlannedSizeBytes { get; }

    /// <summary>
    /// Verdadeiro quando a parte cobre o artefato de origem INTEIRO (nenhum split necessário). Quando
    /// falso, a parte exigiria boundaries de item/pasta — capacidade ainda não disponível neste Passo.
    /// </summary>
    public bool CoversEntireSource { get; }
}

/// <summary>
/// Plano de particionamento (Slice 4B, Passo 2): checkpoint IMUTÁVEL e append-only da INTENÇÃO de
/// particionar um PST. Planejar é read-only — nada neste agregado executa split, rewrite, repair ou cria
/// PST de saída. Um plano só é canônico (reaproveitável em réplay idempotente) quando o desfecho é
/// <see cref="PartitionPlanOutcome.Planned"/> e há partes reais; <see cref="PartitionPlanOutcome.Unsupported"/>
/// e <see cref="PartitionPlanOutcome.Blocked"/> existem apenas como evidência auditável e nunca são
/// reutilizados como se fossem um plano válido.
/// </summary>
public sealed class PartitionPlan
{
    private PartitionPlan(
        PartitionPlanId id,
        TenantId tenant,
        ProjectId project,
        PartitionPlanSource source,
        PartitionPolicy policy,
        PartitionPlannerIdentity planner,
        Sha256Hash planHash,
        PartitionPlanOutcome outcome,
        PartitionPlanReason reason,
        IReadOnlyList<PartitionPlanPart> parts,
        CorrelationId correlation,
        DateTimeOffset plannedAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Source = source;
        Policy = policy;
        Planner = planner;
        PlanHash = planHash;
        Outcome = outcome;
        Reason = reason;
        Parts = parts;
        Correlation = correlation;
        PlannedAtUtc = plannedAtUtc;
    }

    /// <summary>
    /// Cria um plano a partir do MOTIVO (o desfecho é sempre derivado dele — nunca podem divergir) e
    /// valida os invariantes estruturais: um plano <see cref="PartitionPlanOutcome.Planned"/> exige
    /// inspeção canônica, tamanho observado, engine identificada, partes contíguas 1..N cuja soma cobre
    /// exatamente a origem e nenhuma parte acima do limite duro da política; qualquer outro desfecho exige
    /// ZERO partes (nada de boundaries inventados).
    /// </summary>
    /// <exception cref="ArgumentException">Invariante estrutural violado.</exception>
    public static PartitionPlan Create(
        PartitionPlanId id,
        TenantId tenant,
        ProjectId project,
        PartitionPlanSource source,
        PartitionPolicy policy,
        PartitionPlannerIdentity planner,
        Sha256Hash planHash,
        PartitionPlanReason reason,
        IReadOnlyList<PartitionPlanPart> parts,
        CorrelationId correlation,
        DateTimeOffset plannedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(parts);

        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant é obrigatório para planejar.", nameof(tenant));
        }

        if (project.Value == Guid.Empty)
        {
            throw new ArgumentException("Projeto é obrigatório para planejar.", nameof(project));
        }

        var outcome = PartitionPlanReasons.OutcomeOf(reason);
        if (outcome == PartitionPlanOutcome.Planned)
        {
            ValidatePlanned(source, policy, parts);
        }
        else if (parts.Count > 0)
        {
            throw new ArgumentException(
                "Plano não concluído (Unsupported/Blocked) nunca tem partes — boundaries jamais são inventados.",
                nameof(parts));
        }

        return new PartitionPlan(
            id, tenant, project, source, policy, planner, planHash, outcome, reason,
            [.. parts], correlation, plannedAtUtc);
    }

    /// <summary>Reconstrói um plano já persistido (uso exclusivo da camada de persistência).</summary>
    public static PartitionPlan Rehydrate(
        PartitionPlanId id,
        TenantId tenant,
        ProjectId project,
        PartitionPlanSource source,
        PartitionPolicy policy,
        PartitionPlannerIdentity planner,
        Sha256Hash planHash,
        PartitionPlanReason reason,
        IReadOnlyList<PartitionPlanPart> parts,
        CorrelationId correlation,
        DateTimeOffset plannedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new PartitionPlan(
            id, tenant, project, source, policy, planner, planHash,
            PartitionPlanReasons.OutcomeOf(reason), reason, [.. parts], correlation, plannedAtUtc);
    }

    private static void ValidatePlanned(
        PartitionPlanSource source, PartitionPolicy policy, IReadOnlyList<PartitionPlanPart> parts)
    {
        if (source.Inspection is null)
        {
            throw new ArgumentException("Plano concluído exige a identidade da inspeção canônica.", nameof(source));
        }

        if (source.SourceSizeBytes is not { } sourceSize)
        {
            throw new ArgumentException("Plano concluído exige o tamanho observado da origem.", nameof(source));
        }

        if (source.EngineName is null || source.EngineVersion is null)
        {
            throw new ArgumentException("Plano concluído exige nome/versão da engine da inspeção.", nameof(source));
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException("Plano concluído exige ao menos uma parte.", nameof(parts));
        }

        long total = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Sequence != index + 1)
            {
                throw new ArgumentException("Sequências das partes devem ser contíguas e começar em 1.", nameof(parts));
            }

            if (part.PlannedSizeBytes > policy.HardPartBytes)
            {
                throw new ArgumentException("Nenhuma parte planejada pode exceder HardPartBytes.", nameof(parts));
            }

            total += part.PlannedSizeBytes;
        }

        if (total != sourceSize)
        {
            throw new ArgumentException(
                "A soma das partes planejadas deve cobrir exatamente o tamanho observado da origem.", nameof(parts));
        }
    }

    /// <summary>Identidade do plano.</summary>
    public PartitionPlanId Id { get; }

    /// <summary>Tenant do escopo autorizado (nunca inferido do cliente).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado (nunca inferido do cliente).</summary>
    public ProjectId Project { get; }

    /// <summary>Origem do planejamento (artefato, inspeção canônica, hash/tamanho, engine).</summary>
    public PartitionPlanSource Source { get; }

    /// <summary>Política de particionamento aplicada (participa da identidade via fingerprint).</summary>
    public PartitionPolicy Policy { get; }

    /// <summary>Planner que produziu o plano.</summary>
    public PartitionPlannerIdentity Planner { get; }

    /// <summary>Identidade determinística das ENTRADAS do plano (runbook §20.2).</summary>
    public Sha256Hash PlanHash { get; }

    /// <summary>Desfecho, sempre derivado de <see cref="Reason"/>.</summary>
    public PartitionPlanOutcome Outcome { get; }

    /// <summary>Motivo sanitizado do desfecho.</summary>
    public PartitionPlanReason Reason { get; }

    /// <summary>Partes planejadas (vazio em <c>Unsupported</c>/<c>Blocked</c>).</summary>
    public IReadOnlyList<PartitionPlanPart> Parts { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante do planejamento (UTC).</summary>
    public DateTimeOffset PlannedAtUtc { get; }

    /// <summary>
    /// Verdadeiro quando o plano é reaproveitável em réplay idempotente: concluído E com partes reais. É a
    /// única condição sob a qual a store pode reter o plano no índice único filtrado de canonicidade.
    /// </summary>
    public bool IsCanonical => Outcome == PartitionPlanOutcome.Planned && Parts.Count > 0;
}
