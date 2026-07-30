using System.Security.Cryptography;
using System.Text;

namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Calcula hashes SHA-256 determinísticos (mesma entrada canônica ⇒ mesmo hash) usados para
/// versionar configuração de projeto, seleção de onda e artefatos de mapping. A canonicalização
/// usa um separador de unidade (U+001F) que não ocorre em valores válidos.
/// </summary>
public static class DeterministicHash
{
    private const char UnitSeparator = '\u001F';

    /// <summary>Hash determinístico de uma sequência ordenada de partes canônicas.</summary>
    public static Sha256Hash Compute(IEnumerable<string> canonicalParts)
    {
        ArgumentNullException.ThrowIfNull(canonicalParts);
        var canonical = string.Join(UnitSeparator, canonicalParts);
        return ComputeBytes(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>Hash determinístico de um conteúdo binário (ex.: bytes do CSV).</summary>
    public static Sha256Hash ComputeBytes(ReadOnlySpan<byte> data) =>
        new(Convert.ToHexStringLower(SHA256.HashData(data)));
}
