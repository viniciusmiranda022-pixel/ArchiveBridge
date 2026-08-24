using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.WavePartitionBindings;

/// <summary>Identidade de um vínculo wave → output de particionamento, gerada pelo servidor.</summary>
public readonly record struct WavePartitionOutputBindingId(Guid Value)
{
    /// <summary>Gera uma nova identidade de vínculo.</summary>
    public static WavePartitionOutputBindingId New() => new(Guid.NewGuid());
}

/// <summary>
/// Vínculo IMUTÁVEL e append-only entre uma onda aprovada (<see cref="Waves.MigrationWave"/>) e um output
/// canônico de particionamento (<see cref="PstProcessing.PartitionExecutionRecord"/>) — a fonte de
/// AUTORIDADE de custódia física exigida pelo upload Purview (AB-I5-010, desbloqueando AB-I5-009 item 2).
/// <para>
/// <see cref="Waves.WaveSelection"/>/<see cref="Waves.WaveEntry"/> continuam sendo PLANEJAMENTO (metadado
/// declarado na aprovação da onda, nunca revalidado fisicamente); este vínculo é EVIDÊNCIA DE INTEGRAÇÃO
/// entre os bounded contexts Waves × PstProcessing — nunca promove a seleção de onda a prova de custódia,
/// e nunca faz <c>Domain.Waves</c>/<c>Domain.PstProcessing</c> dependerem um do outro (este tipo apenas
/// REFERENCIA os IDs opacos já existentes de ambos).
/// </para>
/// <para>
/// Só pode ser criado (via <see cref="Create"/>, na Application) a partir de uma
/// <see cref="PstProcessing.PartitionExecutionRecord"/> JÁ CANÔNICA (retornada por
/// <c>IPartitionExecutionStore.FindCanonicalAsync</c>) — nunca a partir de identificadores soltos
/// fornecidos pelo chamador: <see cref="Plan"/>, <see cref="Part"/>, <see cref="Execution"/>,
/// <see cref="Artifact"/>, <see cref="PartKey"/>, <see cref="OutputHash"/> e
/// <see cref="OutputSizeBytes"/> são sempre REIDRATADOS do próprio registro de execução, nunca aceitos como
/// argumento independente. Como <c>PartitionExecutionRecord</c> só existe no store DEPOIS que o writer
/// materializou, reabriu/reinspecionou e confirmou o output (Slice 4B, "zero efeito externo"), a mera
/// EXISTÊNCIA do registro canônico já É a prova de "execução concluída e verificada" — não há um enum de
/// status separado a checar (nenhuma linha pendente/em execução/falha é jamais persistida naquela store).
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <c>PurviewSasUploadHandle</c>/
/// <c>CapabilityEvidence</c>): <see cref="Rehydrate"/> recomputa <see cref="BindingHash"/> a partir dos
/// campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record WavePartitionOutputBinding
{
    private WavePartitionOutputBinding(
        WavePartitionOutputBindingId id,
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PartitionPlanId plan,
        PartitionPlanPartId part,
        PartitionExecutionId execution,
        ArtifactId artifact,
        Sha256Hash partKey,
        Sha256Hash outputHash,
        long outputSizeBytes,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc,
        Sha256Hash bindingHash)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Wave = wave;
        Plan = plan;
        Part = part;
        Execution = execution;
        Artifact = artifact;
        PartKey = partKey;
        OutputHash = outputHash;
        OutputSizeBytes = outputSizeBytes;
        Correlation = correlation;
        CreatedAtUtc = createdAtUtc;
        BindingHash = bindingHash;
    }

    /// <summary>
    /// Cria um novo vínculo a partir de uma execução de partição JÁ CANÔNICA. <paramref name="tenant"/>/
    /// <paramref name="project"/> (o escopo autorizado do vínculo) têm de corresponder EXATAMENTE ao
    /// tenant/projeto do próprio <paramref name="execution"/> — nunca um vínculo cross-scope entre a onda e
    /// a execução, mesmo que ambos existam sob o mesmo caller.
    /// </summary>
    /// <exception cref="ArgumentException">Invariante estrutural violado (escopo divergente).</exception>
    public static WavePartitionOutputBinding Create(
        WavePartitionOutputBindingId id,
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PartitionExecutionRecord execution,
        CorrelationId correlation,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (FindStructuralViolation(tenant, project, execution) is { } violation)
        {
            throw new ArgumentException(violation.Message, violation.ParamName);
        }

        var canonicalNowUtc = TruncateToMilliseconds(nowUtc);
        var hash = ComputeBindingHash(
            id, tenant, project, wave, execution.Plan, execution.Part, execution.Id, execution.Artifact,
            execution.PartKey, execution.OutputHash, execution.OutputSizeBytes, correlation, canonicalNowUtc);

        return new WavePartitionOutputBinding(
            id, tenant, project, wave, execution.Plan, execution.Part, execution.Id, execution.Artifact,
            execution.PartKey, execution.OutputHash, execution.OutputSizeBytes, correlation, canonicalNowUtc, hash);
    }

    /// <summary>
    /// Reconstrói um vínculo JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="BindingHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="WavePartitionOutputBindingIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static WavePartitionOutputBinding Rehydrate(
        WavePartitionOutputBindingId id,
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PartitionPlanId plan,
        PartitionPlanPartId part,
        PartitionExecutionId execution,
        ArtifactId artifact,
        Sha256Hash partKey,
        Sha256Hash outputHash,
        long outputSizeBytes,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc,
        Sha256Hash persistedBindingHash)
    {
        var recomputed = ComputeBindingHash(
            id, tenant, project, wave, plan, part, execution, artifact, partKey, outputHash, outputSizeBytes,
            correlation, createdAtUtc);
        if (!string.Equals(recomputed.Value, persistedBindingHash.Value, StringComparison.Ordinal))
        {
            throw new WavePartitionOutputBindingIntegrityViolationException(
                $"O binding_hash persistido para {id.Value} não corresponde ao hash recomputado a partir dos " +
                "campos carregados — vínculo possivelmente adulterado ou corrompido.");
        }

        return new WavePartitionOutputBinding(
            id, tenant, project, wave, plan, part, execution, artifact, partKey, outputHash, outputSizeBytes,
            correlation, createdAtUtc, persistedBindingHash);
    }

    /// <summary>
    /// Verdadeiro quando <paramref name="other"/> representa exatamente o MESMO output canônico para a
    /// MESMA onda/plano/parte (convergência idempotente, item 4) — compara apenas o CONTEÚDO estável
    /// (nunca <see cref="Id"/>/<see cref="Correlation"/>/<see cref="CreatedAtUtc"/>, que mudam a cada
    /// tentativa mesmo sem mudança real).
    /// </summary>
    public bool IsSameLogicalOutputAs(WavePartitionOutputBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Tenant == other.Tenant
            && Project == other.Project
            && Wave == other.Wave
            && Plan == other.Plan
            && Part == other.Part
            && Execution == other.Execution
            && Artifact == other.Artifact
            && PartKey == other.PartKey
            && OutputHash == other.OutputHash
            && OutputSizeBytes == other.OutputSizeBytes;
    }

    private readonly record struct StructuralViolation(string Message, string ParamName);

    private static StructuralViolation? FindStructuralViolation(TenantId tenant, ProjectId project, PartitionExecutionRecord execution)
    {
        if (tenant.Value == Guid.Empty)
        {
            return new StructuralViolation("Tenant é obrigatório para vincular.", nameof(tenant));
        }

        if (project.Value == Guid.Empty)
        {
            return new StructuralViolation("Projeto é obrigatório para vincular.", nameof(project));
        }

        // Defesa em profundidade: o vínculo nunca pode cruzar o escopo da execução que o originou, mesmo
        // que a Application já tenha resolvido ambos sob o mesmo TenantScope — o Domain não confia
        // cegamente na chamada e recusa estruturalmente qualquer divergência.
        if (execution.Tenant != tenant || execution.Project != project)
        {
            return new StructuralViolation(
                "A execução de partição referenciada não pertence ao mesmo tenant/projeto do vínculo.", nameof(execution));
        }

        return null;
    }

    /// <summary>Identidade do vínculo.</summary>
    public WavePartitionOutputBindingId Id { get; }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Plano de particionamento cuja parte foi materializada.</summary>
    public PartitionPlanId Plan { get; }

    /// <summary>Parte planejada materializada.</summary>
    public PartitionPlanPartId Part { get; }

    /// <summary>Execução canônica (checkpoint verificado) que este vínculo referencia.</summary>
    public PartitionExecutionId Execution { get; }

    /// <summary>Artefato de origem sob custódia, reidratado da execução — nunca informado pelo caller.</summary>
    public ArtifactId Artifact { get; }

    /// <summary>Chave opaca determinística da parte, reidratada da execução (defesa em profundidade).</summary>
    public Sha256Hash PartKey { get; }

    /// <summary>Hash SHA-256 do output canônico no momento do vínculo, reidratado da execução.</summary>
    public Sha256Hash OutputHash { get; }

    /// <summary>Tamanho do output canônico, em bytes, reidratado da execução.</summary>
    public long OutputSizeBytes { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que o vínculo foi criado (append-only — nunca mutado depois).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Hash determinístico de todos os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash BindingHash { get; }

    private static Sha256Hash ComputeBindingHash(
        WavePartitionOutputBindingId id, TenantId tenant, ProjectId project, WaveId wave, PartitionPlanId plan,
        PartitionPlanPartId part, PartitionExecutionId execution, ArtifactId artifact, Sha256Hash partKey,
        Sha256Hash outputHash, long outputSizeBytes, CorrelationId correlation, DateTimeOffset createdAtUtc) =>
        DeterministicHash.Compute(
        [
            id.Value.ToString("N"),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plan.Value.ToString("N"),
            part.Value.ToString("N"),
            execution.Value.ToString("N"),
            artifact.Value.ToString("N"),
            partKey.Value,
            outputHash.Value,
            outputSizeBytes.ToString(CultureInfo.InvariantCulture),
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
