using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>Identidade de uma execução de parte, gerada pelo servidor.</summary>
public readonly record struct PartitionExecutionId(Guid Value)
{
    /// <summary>Gera uma nova identidade de execução.</summary>
    public static PartitionExecutionId New() => new(Guid.NewGuid());
}

/// <summary>Nome/versão do executor que materializou a parte (participa da evidência/auditoria).</summary>
public sealed record PartitionExecutorIdentity
{
    /// <summary>Cria a identidade do executor, validando forma e tamanho.</summary>
    /// <exception cref="ArgumentException">Nome/versão vazios, com caractere de controle ou longos demais.</exception>
    public PartitionExecutorIdentity(string name, string version)
    {
        Name = TextValue.Require(name, nameof(name), 100);
        Version = TextValue.Require(version, nameof(version), 50);
    }

    /// <summary>Nome estável do executor (evidência/auditoria).</summary>
    public string Name { get; }

    /// <summary>Versão do executor (evidência/auditoria).</summary>
    public string Version { get; }
}

/// <summary>
/// Checkpoint IMUTÁVEL e append-only de UMA parte planejada efetivamente EXECUTADA e VERIFICADA (Slice 4B,
/// Passo 3): o manifesto durável do <c>PartCreated</c> do runbook §20.4. Uma linha só existe aqui depois que
/// o writer materializou o output, reabriu/reinspecionou os bytes REALMENTE escritos e confirmou que
/// batem — nunca antes. Diferente de <see cref="PartitionPlan"/> e <see cref="PstInspectionRecord"/>, esta
/// tabela nunca guarda tentativas fracassadas/rejeitadas como evidência: um plano inelegível, uma origem
/// obsoleta, cancelamento, timeout ou falta de espaço nunca chegam a produzir uma linha (§ "zero efeito
/// externo" do work order AB-4B-006) — a rejeição em si já é auditável pelo <see cref="PartitionPlan"/> ou
/// pela trilha de log/exceção do chamador, sem precisar de uma segunda linha de evidência aqui.
/// <para>
/// Neste Passo, o ÚNICO caso autorizado a executar é <see cref="PartitionPlanReason.SinglePartWithinTarget"/>:
/// a parte cobre o artefato de origem INTEIRO, então o invariante estrutural é forte e verificável
/// independentemente da implementação de infraestrutura — <see cref="OutputHash"/> e
/// <see cref="OutputSizeBytes"/> têm de ser EXATAMENTE <see cref="SourceHash"/>/<see cref="SourceSizeBytes"/>.
/// Isso é reforçado aqui no Domain (defesa em profundidade) e também no banco (CHECK constraint da
/// migration) — nenhuma das duas camadas confia cegamente na outra.
/// </para>
/// </summary>
public sealed class PartitionExecutionRecord
{
    private PartitionExecutionRecord(
        PartitionExecutionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        PartitionPlanId plan,
        PartitionPlanPartId part,
        Sha256Hash planHash,
        int partSequence,
        Sha256Hash partKey,
        Sha256Hash sourceHash,
        long sourceSizeBytes,
        Sha256Hash outputHash,
        long outputSizeBytes,
        PartitionExecutorIdentity executor,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Artifact = artifact;
        Plan = plan;
        Part = part;
        PlanHash = planHash;
        PartSequence = partSequence;
        PartKey = partKey;
        SourceHash = sourceHash;
        SourceSizeBytes = sourceSizeBytes;
        OutputHash = outputHash;
        OutputSizeBytes = outputSizeBytes;
        Executor = executor;
        Correlation = correlation;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    /// <summary>
    /// Cria o registro de uma execução CONCLUÍDA e VERIFICADA. Só deve ser chamado depois que o output foi
    /// escrito, reaberto/reinspecionado e confirmado — nunca antes ("<c>PartCreated</c> somente após
    /// sucesso", runbook §20.4).
    /// </summary>
    /// <exception cref="ArgumentException">Invariante estrutural violado.</exception>
    public static PartitionExecutionRecord Complete(
        PartitionExecutionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        PartitionPlanId plan,
        PartitionPlanPartId part,
        Sha256Hash planHash,
        int partSequence,
        Sha256Hash partKey,
        Sha256Hash sourceHash,
        long sourceSizeBytes,
        Sha256Hash outputHash,
        long outputSizeBytes,
        PartitionExecutorIdentity executor,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(executor);

        if (FindStructuralViolation(
                tenant, project, partSequence, planHash, partKey, sourceHash, sourceSizeBytes,
                outputHash, outputSizeBytes, startedAtUtc, completedAtUtc) is { } violation)
        {
            throw new ArgumentException(violation.Message, violation.ParamName);
        }

        return new PartitionExecutionRecord(
            id, tenant, project, artifact, plan, part, planHash, partSequence, partKey, sourceHash,
            sourceSizeBytes, outputHash, outputSizeBytes, executor, correlation, startedAtUtc, completedAtUtc);
    }

    /// <summary>
    /// Reconstrói uma execução JÁ PERSISTIDA (uso exclusivo da camada de persistência). A persistência é uma
    /// fronteira NÃO CONFIÁVEL: reaplica os MESMOS invariantes estruturais de <see cref="Complete"/> — uma
    /// linha corrompida ou adulterada falha fechado em vez de ser devolvida/reaproveitada em réplay.
    /// </summary>
    /// <exception cref="PartitionExecutionIntegrityViolationException">
    /// A linha persistida viola um invariante estrutural do agregado.
    /// </exception>
    public static PartitionExecutionRecord Rehydrate(
        PartitionExecutionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        PartitionPlanId plan,
        PartitionPlanPartId part,
        Sha256Hash planHash,
        int partSequence,
        Sha256Hash partKey,
        Sha256Hash sourceHash,
        long sourceSizeBytes,
        Sha256Hash outputHash,
        long outputSizeBytes,
        PartitionExecutorIdentity executor,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(executor);

        if (FindStructuralViolation(
                tenant, project, partSequence, planHash, partKey, sourceHash, sourceSizeBytes,
                outputHash, outputSizeBytes, startedAtUtc, completedAtUtc) is { } violation)
        {
            throw new PartitionExecutionIntegrityViolationException(
                $"Execução persistida viola um invariante estrutural do Domain: {violation.Message}");
        }

        return new PartitionExecutionRecord(
            id, tenant, project, artifact, plan, part, planHash, partSequence, partKey, sourceHash,
            sourceSizeBytes, outputHash, outputSizeBytes, executor, correlation, startedAtUtc, completedAtUtc);
    }

    /// <summary>
    /// Verdadeiro quando <see cref="PartKey"/> é EXATAMENTE a chave opaca determinística recalculada a
    /// partir de <see cref="PlanHash"/> e <see cref="PartSequence"/>. Defesa em profundidade, independente
    /// da implementação de store.
    /// </summary>
    public bool HasConsistentIdentity() => PartitionPlanIdentity.ComputePartKey(PlanHash, PartSequence) == PartKey;

    /// <summary>Invariante estrutural violado, já sanitizado (nunca carrega caminho, UPN ou conteúdo).</summary>
    private readonly record struct StructuralViolation(string Message, string ParamName);

    /// <summary>
    /// Caminho ÚNICO de validação dos invariantes estruturais do agregado, compartilhado por
    /// <see cref="Complete"/> e <see cref="Rehydrate"/>. Devolve <see langword="null"/> quando válido.
    /// </summary>
    private static StructuralViolation? FindStructuralViolation(
        TenantId tenant,
        ProjectId project,
        int partSequence,
        Sha256Hash planHash,
        Sha256Hash partKey,
        Sha256Hash sourceHash,
        long sourceSizeBytes,
        Sha256Hash outputHash,
        long outputSizeBytes,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        if (tenant.Value == Guid.Empty)
        {
            return new StructuralViolation("Tenant é obrigatório para executar.", nameof(tenant));
        }

        if (project.Value == Guid.Empty)
        {
            return new StructuralViolation("Projeto é obrigatório para executar.", nameof(project));
        }

        if (partSequence < 1)
        {
            return new StructuralViolation("Sequência de parte começa em 1.", nameof(partSequence));
        }

        if (PartitionPlanIdentity.ComputePartKey(planHash, partSequence) != partKey)
        {
            return new StructuralViolation(
                "A part_key não corresponde à chave opaca determinística derivada do plano e da sequência.",
                nameof(partKey));
        }

        if (sourceSizeBytes < 0)
        {
            return new StructuralViolation("Tamanho de origem não pode ser negativo.", nameof(sourceSizeBytes));
        }

        if (outputSizeBytes < 0)
        {
            return new StructuralViolation("Tamanho de saída não pode ser negativo.", nameof(outputSizeBytes));
        }

        // Invariante central do único caso autorizado neste Passo (SinglePartWithinTarget cobre a origem
        // INTEIRA): a saída tem de ser cópia byte-for-byte exata da origem — mesmo hash, mesmo tamanho.
        // Qualquer divergência significa que a execução não produziu o que o plano exigia.
        if (outputHash != sourceHash || outputSizeBytes != sourceSizeBytes)
        {
            return new StructuralViolation(
                "A saída não é cópia byte-for-byte da origem (hash/tamanho divergente do esperado para SinglePartWithinTarget).",
                nameof(outputHash));
        }

        if (completedAtUtc < startedAtUtc)
        {
            return new StructuralViolation("completedAtUtc não pode ser anterior a startedAtUtc.", nameof(completedAtUtc));
        }

        return null;
    }

    /// <summary>Identidade da execução.</summary>
    public PartitionExecutionId Id { get; }

    /// <summary>Tenant do escopo autorizado (nunca inferido do cliente).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado (nunca inferido do cliente).</summary>
    public ProjectId Project { get; }

    /// <summary>Artefato de origem sob custódia.</summary>
    public ArtifactId Artifact { get; }

    /// <summary>Plano de particionamento executado.</summary>
    public PartitionPlanId Plan { get; }

    /// <summary>Parte planejada materializada.</summary>
    public PartitionPlanPartId Part { get; }

    /// <summary>Identidade determinística do plano (runbook §20.2) — liga a execução ao plano exato.</summary>
    public Sha256Hash PlanHash { get; }

    /// <summary>Sequência da parte dentro do plano (1..N).</summary>
    public int PartSequence { get; }

    /// <summary>Chave opaca determinística da parte executada.</summary>
    public Sha256Hash PartKey { get; }

    /// <summary>Hash SHA-256 da origem no momento da execução.</summary>
    public Sha256Hash SourceHash { get; }

    /// <summary>Tamanho da origem, em bytes.</summary>
    public long SourceSizeBytes { get; }

    /// <summary>Hash SHA-256 do output REALMENTE escrito, confirmado por reabertura/reinspeção.</summary>
    public Sha256Hash OutputHash { get; }

    /// <summary>Tamanho do output REALMENTE escrito, em bytes, confirmado por reabertura/reinspeção.</summary>
    public long OutputSizeBytes { get; }

    /// <summary>Executor (nome/versão) que materializou a parte.</summary>
    public PartitionExecutorIdentity Executor { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Início da execução (UTC).</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Conclusão/verificação da execução (UTC) — o instante do <c>PartCreated</c>.</summary>
    public DateTimeOffset CompletedAtUtc { get; }
}
