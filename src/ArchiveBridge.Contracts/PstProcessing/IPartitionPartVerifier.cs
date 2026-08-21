using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Porta de REVALIDAÇÃO física, somente leitura, de uma <see cref="PartitionExecutionRecord"/> JÁ PERSISTIDA
/// como canônica (Slice 4B, Passo 3 — correção AB-4B-007). Diferente de <see cref="IPartitionPartWriter"/>,
/// que verifica o output que ELE PRÓPRIO acabou de materializar, este verificador revalida em RÉPLAY um
/// bundle publicado por uma execução ANTERIOR — depois que o checkpoint SQL já existe — antes de a
/// Application devolvê-lo como resultado. Tampering, corrupção ou remoção do output após o checkpoint SQL
/// tem de ser detectado aqui, nunca silenciosamente ignorado (a persistência sozinha nunca é prova
/// suficiente de que o output ainda existe/confere).
/// <para>
/// Implementações são estritamente SOMENTE LEITURA: nunca criam, sobrescrevem ou regeneram o output. Output
/// ausente é tratado como adulteração (fail-closed), nunca como "precisa recriar".
/// </para>
/// </summary>
public interface IPartitionPartVerifier
{
    /// <summary>
    /// Revalida o bundle de output correspondente à <paramref name="execution"/> persistida. Devolve
    /// normalmente quando o bundle confere byte-for-byte com o registro persistido e o manifesto é
    /// estruturalmente consistente com ele.
    /// </summary>
    /// <exception cref="PartitionExecutionOutputTamperedException">
    /// Output ausente, com entrada inesperada, sidecar/manifesto divergente, ou hash/tamanho divergente do
    /// registro persistido — fail-closed, o output nunca é reconstruído.
    /// </exception>
    Task VerifyAsync(TenantScope scope, PartitionExecutionRecord execution, CancellationToken cancellationToken);
}
