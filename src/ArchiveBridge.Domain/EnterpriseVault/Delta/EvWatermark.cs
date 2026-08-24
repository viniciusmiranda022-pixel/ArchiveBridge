using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>Motivo estruturado de rejeição de um watermark candidato (AB-4C-008 req 13) — fail-closed, nunca uma mensagem livre.</summary>
public enum EvWatermarkRejectionReason
{
    /// <summary>O candidato não é mais recente que o watermark canônico já aceito (stale/replay).</summary>
    Stale,

    /// <summary>Tenant/projeto/connector/archive do candidato diverge do watermark canônico.</summary>
    CrossScope,

    /// <summary>O candidato foi emitido por outra strategy (nome distinto) — nunca combinado com o watermark anterior.</summary>
    StrategyMismatch,

    /// <summary>Mesma strategy, mas versão inferior à já aceita — downgrade recusado.</summary>
    StrategyDowngrade,

    /// <summary>O hash de lineage persistido não corresponde aos campos realmente carregados — evidência adulterada.</summary>
    Tampered,
}

/// <summary>
/// Watermark OPACO e versionado (AB-4C-008 req 3/4): Domain/Application conhecem apenas a LINEAGE
/// (tenant/projeto/connector/archive/fase/strategy/execução que o produziu) — o conteúdo de
/// <see cref="OpaqueToken"/> é emitido e interpretado EXCLUSIVAMENTE pelo adapter EV da strategy
/// selecionada; nunca decodificado aqui. É responsabilidade do adapter garantir que o token não carregue
/// credencial, conteúdo de mailbox ou PII desnecessária antes de entregá-lo ao Domain.
/// </summary>
public sealed record EvWatermark
{
    private const int OpaqueTokenMaxLength = 4000;

    private EvWatermark(
        WatermarkId id,
        TenantId tenant,
        ProjectId project,
        ConnectorId connector,
        string externalArchiveId,
        EvDeltaPhase phase,
        EvDeltaStrategyId strategy,
        Guid producingExecutionId,
        string opaqueToken,
        DateTimeOffset issuedAtUtc,
        Sha256Hash lineageHash)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Connector = connector;
        ExternalArchiveId = externalArchiveId;
        Phase = phase;
        Strategy = strategy;
        ProducingExecutionId = producingExecutionId;
        OpaqueToken = opaqueToken;
        IssuedAtUtc = issuedAtUtc;
        LineageHash = lineageHash;
    }

    /// <summary>Emite um novo watermark a partir do token opaco produzido pelo adapter EV da strategy selecionada.</summary>
    public static EvWatermark Issue(
        TenantId tenant,
        ProjectId project,
        ConnectorId connector,
        string externalArchiveId,
        EvDeltaPhase phase,
        EvDeltaStrategyId strategy,
        Guid producingExecutionId,
        string opaqueToken,
        DateTimeOffset issuedAtUtc)
    {
        var sanitizedArchiveId = TextValue.Require(externalArchiveId, nameof(externalArchiveId), 300);
        var sanitizedToken = TextValue.Require(opaqueToken, nameof(opaqueToken), OpaqueTokenMaxLength);
        if (producingExecutionId == Guid.Empty)
        {
            throw new ArgumentException("producingExecutionId é obrigatório.", nameof(producingExecutionId));
        }

        // Canonicaliza ANTES de hashear/persistir (AB-4C-009 fix): a coluna ev_watermarks.issued_at_utc é
        // DATETIME2(3) — deixar um resto de sub-milissegundo aqui e só truncar dentro do hash arriscaria
        // divergir do que o SQL Server efetivamente grava (arredondamento vs truncamento na conversão de
        // precisão são comportamentos distintos); zerando o resto já em memória, não sobra nada para o SQL
        // Server arredondar, então o valor persistido é bit-a-bit igual ao usado para computar o hash.
        var canonicalIssuedAtUtc = TruncateToMilliseconds(issuedAtUtc);
        var lineageHash = ComputeLineageHash(
            tenant, project, connector, sanitizedArchiveId, phase, strategy, producingExecutionId, sanitizedToken, canonicalIssuedAtUtc);
        return new EvWatermark(
            WatermarkId.New(), tenant, project, connector, sanitizedArchiveId, phase, strategy,
            producingExecutionId, sanitizedToken, canonicalIssuedAtUtc, lineageHash);
    }

    /// <summary>Reconstrói um watermark já persistido, revalidando o hash de lineage contra os campos REALMENTE carregados (fail-closed).</summary>
    public static EvWatermark Rehydrate(
        WatermarkId id,
        TenantId tenant,
        ProjectId project,
        ConnectorId connector,
        string externalArchiveId,
        EvDeltaPhase phase,
        EvDeltaStrategyId strategy,
        Guid producingExecutionId,
        string opaqueToken,
        DateTimeOffset issuedAtUtc,
        Sha256Hash persistedLineageHash)
    {
        var lineageHash = ComputeLineageHash(
            tenant, project, connector, externalArchiveId, phase, strategy, producingExecutionId, opaqueToken, issuedAtUtc);
        if (!string.Equals(lineageHash.Value, persistedLineageHash.Value, StringComparison.Ordinal))
        {
            throw new EvWatermarkRejectedException(
                EvWatermarkRejectionReason.Tampered,
                "O hash de lineage persistido do watermark não corresponde aos campos carregados (fail-closed).");
        }

        return new EvWatermark(
            id, tenant, project, connector, externalArchiveId, phase, strategy,
            producingExecutionId, opaqueToken, issuedAtUtc, lineageHash);
    }

    /// <summary>Identidade opaca do watermark.</summary>
    public WatermarkId Id { get; }

    /// <summary>Tenant do escopo (lineage).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo (lineage).</summary>
    public ProjectId Project { get; }

    /// <summary>Connector que produziu o watermark (lineage).</summary>
    public ConnectorId Connector { get; }

    /// <summary>Archive externo opaco a que este watermark pertence (lineage).</summary>
    public string ExternalArchiveId { get; }

    /// <summary>Fase que produziu este watermark.</summary>
    public EvDeltaPhase Phase { get; }

    /// <summary>Strategy (nome+versão) que emitiu este watermark.</summary>
    public EvDeltaStrategyId Strategy { get; }

    /// <summary>Execução (run) que produziu este watermark — correlação com a evidência da fase (req 4).</summary>
    public Guid ProducingExecutionId { get; }

    /// <summary>Conteúdo opaco, interpretável somente pelo adapter EV da strategy — nunca pelo Domain/Application.</summary>
    public string OpaqueToken { get; }

    /// <summary>Instante de emissão (UTC) — usado apenas para ordenar/recusar stale, nunca como critério de delta.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>
    /// Hash determinístico de TODOS os campos que definem identidade/ordem/conteúdo persistidos do
    /// watermark (tenant/projeto/connector/archive/fase/strategy + <see cref="ProducingExecutionId"/> +
    /// <see cref="OpaqueToken"/> + <see cref="IssuedAtUtc"/>, este último canonicalizado em milissegundos —
    /// a mesma precisão de <c>ev_watermarks.issued_at_utc DATETIME2(3)</c>, para que o hash calculado na
    /// emissão sobreviva ao arredondamento do SQL Server e continue batendo na releitura) — detecta
    /// adulteração de QUALQUER um desses campos entre persistência e leitura (AB-4C-009 fix). O nome do
    /// campo permanece "lineage" por compatibilidade com a coluna <c>lineage_hash</c> já publicada; o
    /// conteúdo coberto é a evidência completa do watermark, não apenas a lineage de escopo.
    /// </summary>
    public Sha256Hash LineageHash { get; }

    /// <summary>
    /// Garante que ESTE watermark pode preceder um novo delta para o MESMO tenant/projeto/connector/archive,
    /// com a MESMA strategy (nunca versão inferior) — fail-closed em qualquer divergência (req 13).
    /// </summary>
    public void EnsureCanPrecede(TenantId tenant, ProjectId project, ConnectorId connector, string externalArchiveId, EvDeltaStrategyId nextStrategy)
    {
        if (!Tenant.Equals(tenant) || !Project.Equals(project) || !Connector.Equals(connector)
            || !string.Equals(ExternalArchiveId, externalArchiveId, StringComparison.Ordinal))
        {
            throw new EvWatermarkRejectedException(
                EvWatermarkRejectionReason.CrossScope,
                "Watermark pertence a outro tenant/projeto/connector/archive (fail-closed).");
        }

        if (!string.Equals(Strategy.Name, nextStrategy.Name, StringComparison.Ordinal))
        {
            throw new EvWatermarkRejectedException(
                EvWatermarkRejectionReason.StrategyMismatch,
                "Watermark foi emitido por outra delta strategy (fail-closed).");
        }

        if (nextStrategy.Version < Strategy.Version)
        {
            throw new EvWatermarkRejectedException(
                EvWatermarkRejectionReason.StrategyDowngrade,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Downgrade de strategy recusado: watermark canônico está em v{0}, delta solicitado em v{1}.",
                    Strategy.Version, nextStrategy.Version));
        }
    }

    /// <summary>Garante que <paramref name="candidate"/> é estritamente mais recente que este watermark canônico (recusa stale/replay).</summary>
    public void EnsureSucceededBy(EvWatermark candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.IssuedAtUtc <= IssuedAtUtc)
        {
            throw new EvWatermarkRejectedException(
                EvWatermarkRejectionReason.Stale,
                "Watermark candidato não é mais recente que o watermark canônico atual (fail-closed).");
        }
    }

    private static Sha256Hash ComputeLineageHash(
        TenantId tenant, ProjectId project, ConnectorId connector, string externalArchiveId,
        EvDeltaPhase phase, EvDeltaStrategyId strategy, Guid producingExecutionId, string opaqueToken, DateTimeOffset issuedAtUtc) =>
        DeterministicHash.Compute(
        [
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            connector.Value.ToString("N"),
            externalArchiveId,
            phase.ToString(),
            strategy.Name,
            strategy.Version.ToString(CultureInfo.InvariantCulture),
            producingExecutionId.ToString("N"),
            opaqueToken,
            TruncateToMilliseconds(issuedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>
    /// Trunca para milissegundos (mesma precisão de <c>ev_watermarks.issued_at_utc DATETIME2(3)</c>) — usada
    /// tanto para canonicalizar <see cref="IssuedAtUtc"/> ANTES de persistir (<see cref="Issue"/>) quanto,
    /// defensivamente, dentro do próprio hash (idempotente sobre um valor já truncado que veio da releitura
    /// em <see cref="Rehydrate"/>).
    /// </summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
