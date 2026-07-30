using System.Text;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Artefato de mapping gerado: o texto CSV (UTF-8, sem BOM), os bytes correspondentes e o SHA-256 do
/// conteúdo (o <c>mapping.sha256</c> de evidência). Imutável — o conteúdo aprovado nunca é editado;
/// uma alteração exige nova geração (nova versão, novo hash).
/// </summary>
public sealed class MappingDocument
{
    private readonly byte[] contentBytes;

    internal MappingDocument(string content, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        RowCount = rowCount;

        // UTF-8 sem BOM: o serviço de importação espera UTF-8 puro.
        contentBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        ContentSha256 = DeterministicHash.ComputeBytes(contentBytes);
    }

    /// <summary>Texto integral do CSV (cabeçalho + linhas, terminadores CRLF).</summary>
    public string Content { get; }

    /// <summary>Número de linhas de dados (sem o cabeçalho).</summary>
    public int RowCount { get; }

    /// <summary>SHA-256 dos bytes UTF-8 do CSV (evidência <c>mapping.sha256</c>).</summary>
    public Sha256Hash ContentSha256 { get; }

    /// <summary>Bytes UTF-8 do CSV (cópia somente-leitura), prontos para persistência de artefato.</summary>
    public ReadOnlyMemory<byte> GetBytes() => contentBytes;
}

/// <summary>
/// Resultado de uma geração de mapping: o documento, a evidência de versão e as linhas autorizadas
/// (para persistência da evidência linha a linha). As linhas nunca contêm segredo nem conteúdo.
/// </summary>
public sealed record MappingGenerationResult(
    MappingDocument Document, MappingCsvVersion Version, IReadOnlyList<MappingRow> Rows);
