using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion;

namespace ArchiveBridge.Contracts.TargetIngestion;

/// <summary>
/// Porta de ingestão no destino Microsoft 365 (ADR-0006). O adapter concreto (Purview GA,
/// Graph condicional) é implementado na Infrastructure e nunca expõe tipos do SDK do fornecedor.
/// A capability governa o que pode rodar: uma rota <see cref="CapabilityState.BlockedPendingEvidence"/>
/// não executa. Não há transação distribuída com o serviço externo — o efeito é conciliado pelo ledger.
/// </summary>
public interface ITargetIngestor
{
    /// <summary>Estado de capability desta rota de destino (catálogo de adapters, ADR-0006/0007).</summary>
    CapabilityState Capability { get; }

    /// <summary>
    /// Submete a ingestão de forma idempotente pela <see cref="OperationKey"/> (gerada antes do
    /// efeito). Um resultado <see cref="ExternalOperationState.Ambiguous"/> exige ação de operador,
    /// nunca repetição automática.
    /// </summary>
    Task<ExternalOperationState> SubmitAsync(OperationKey key, ArtifactId artifact, CancellationToken cancellationToken);
}
