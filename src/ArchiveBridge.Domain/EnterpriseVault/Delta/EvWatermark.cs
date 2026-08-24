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

        var lineageHash = ComputeLineageHash(tenant, project, connector, sanitizedArchiveId, phase, strategy);
        return new EvWatermark(
            WatermarkId.New(), tenant, project, connector, sanitizedArchiveId, phase, strategy,
            producingExecutionId, sanitizedToken, issuedAtUtc, lineageHash);
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
        var lineageHash = ComputeLineageHash(tenant, project, connector, externalArchiveId, phase, strategy);
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

    /// <summary>Hash determinístico da lineage — detecta adulteração entre persistência e leitura.</summary>
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
        EvDeltaPhase phase, EvDeltaStrategyId strategy) =>
        DeterministicHash.Compute(
        [
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            connector.Value.ToString("N"),
            externalArchiveId,
            phase.ToString(),
            strategy.Name,
            strategy.Version.ToString(CultureInfo.InvariantCulture),
        ]);
}
