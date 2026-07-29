using ArchiveBridge.Domain.TargetIngestion;

namespace ArchiveBridge.Contracts.TargetIngestion;

/// <summary>
/// Porta do ledger <c>external_operations</c> (ADR-0003): registra o intent antes de qualquer
/// efeito externo e concilia o resultado observado. Implementado na Infrastructure (SQL local).
/// </summary>
public interface IExternalOperationLedger
{
    /// <summary>Registra o intent (<see cref="OperationKey"/>) antes do efeito externo.</summary>
    Task RecordIntentAsync(OperationKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Concilia o estado observado do efeito externo. <see cref="ExternalOperationState.Ambiguous"/>
    /// nunca é resolvido por repetição automática — requer decisão de operador.
    /// </summary>
    Task ReconcileAsync(OperationKey key, ExternalOperationState observed, CancellationToken cancellationToken);
}
