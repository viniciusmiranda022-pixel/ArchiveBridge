using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Mapping;

/// <summary>
/// Porta de persistência das versões de mapping e da sua evidência (linhas + sha256). Uma nova
/// geração nunca sobrescreve: insere a versão N+1 como utilizável e marca a anterior como
/// substituída (Superseded) — evidência preservada. Não persiste segredo nem conteúdo de e-mail/PST
/// (apenas metadados do CSV e o hash). O conteúdo bruto do CSV é entregue ao chamador como artefato.
/// </summary>
public interface IMappingStore
{
    /// <summary>Maior <c>mapping_version</c> já gerada para a onda (0 se nenhuma), para calcular N+1.</summary>
    Task<int> GetMaxVersionAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>
    /// Versão utilizável corrente da onda (<see langword="null"/> se nenhuma). Usada para tornar a
    /// geração idempotente em retry: se já existe uma versão utilizável para a mesma seleção, não se
    /// gera outra.
    /// </summary>
    Task<MappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste atomicamente a nova versão: dentro da transação, calcula o próximo número de versão
    /// (N+1) sob lock (sequência atômica, sem TOCTOU), substitui a versão utilizável anterior
    /// (Superseded) e grava a nova versão e suas linhas de evidência. O índice único filtrado garante
    /// no máximo uma versão utilizável por onda (fail-closed). Retorna a evidência com a versão
    /// efetivamente atribuída.
    /// </summary>
    Task<MappingCsvVersion> SaveAsync(TenantScope scope, MappingGenerationResult result, CancellationToken cancellationToken);
}
