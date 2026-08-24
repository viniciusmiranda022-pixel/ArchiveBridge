using System.Globalization;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Delta;

/// <summary>
/// Adapter EV substituível (AB-4C-008 req 7) da strategy <c>EV-COMPOSITE-WATERMARK@v1</c>: emite um
/// watermark OPACO composto por uma sequência monotônica + lineage de execução — NUNCA <c>ReceivedDate</c>
/// isolado como único critério (STOP-THE-LINE, runbook §16.5). Classificação <see cref="EvDeltaStrategyCertification.Compatible"/>
/// (ADR-0013, honestidade comercial): a arquitetura comporta esta strategy para as famílias EV
/// documentadas (<see cref="EvDeltaStrategyCatalog"/>), mas nenhuma chamada real a um host EV foi ainda
/// validada em laboratório. A emissão real do watermark contra o Enterprise Vault (reaproveitando o mesmo
/// mecanismo PowerShell/<c>Export-EVArchive</c> do Passo 2, com o filtro incremental aprovado) é
/// responsabilidade de um Passo POSTERIOR de certificação — esta classe é o ponto de substituição
/// (Connector Host) que esse trabalho deverá popular, mantendo o MESMO <see cref="StrategyId"/> e contrato.
/// </summary>
public sealed class EvCompositeWatermarkDeltaStrategyAdapter : IEvDeltaStrategyAdapter
{
    private const string TokenPrefix = "ev-composite-watermark/v1";

    /// <inheritdoc />
    public EvDeltaStrategyId StrategyId { get; } = new("EV-COMPOSITE-WATERMARK", 1);

    /// <inheritdoc />
    public Task<EvWatermarkIssueResult> IssueBaselineWatermarkAsync(EvDeltaBaselineIssueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = EncodeToken(sequence: 0, request.ExecutionId);
        return Task.FromResult(new EvWatermarkIssueResult(token, request.EvVersionDisplay));
    }

    /// <inheritdoc />
    public Task<EvWatermarkIssueResult> IssueIncrementalWatermarkAsync(EvDeltaIncrementIssueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previousSequence = DecodeSequence(request.Previous.OpaqueToken);
        var token = EncodeToken(previousSequence + 1, request.ExecutionId);
        return Task.FromResult(new EvWatermarkIssueResult(token, request.EvVersionDisplay));
    }

    private static string EncodeToken(long sequence, Guid executionId) =>
        $"{TokenPrefix};seq={sequence.ToString(CultureInfo.InvariantCulture)};exec={executionId:N}";

    private static long DecodeSequence(string opaqueToken)
    {
        if (opaqueToken is null || !opaqueToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Watermark anterior não foi emitido pelo adapter EV-COMPOSITE-WATERMARK — token opaco incompatível (fail-closed).");
        }

        var segments = opaqueToken.Split(';');
        foreach (var segment in segments)
        {
            if (segment.StartsWith("seq=", StringComparison.Ordinal)
                && long.TryParse(segment.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
            {
                return sequence;
            }
        }

        throw new InvalidOperationException("Watermark anterior está malformado — segmento de sequência ausente/inválido (fail-closed).");
    }
}
