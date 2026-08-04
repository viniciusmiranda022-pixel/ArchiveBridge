using System.Buffers.Binary;

namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Token de concorrência otimista (o <c>ROWVERSION</c> de 8 bytes do SQL Server, big-endian). É
/// carregado na leitura do agregado e conferido em cada UPDATE (<c>WHERE row_version = @expected</c>):
/// se a linha mudou desde a leitura, nenhuma linha é afetada e o store falha de forma explícita
/// (lost update evitado). <see cref="None"/> representa um agregado ainda não persistido.
/// </summary>
public readonly record struct RowVersion(ulong Value)
{
    /// <summary>Token nulo (agregado ainda não persistido).</summary>
    public static RowVersion None => new(0);

    /// <summary>Verdadeiro quando há um token válido (agregado já persistido).</summary>
    public bool IsPersisted => Value != 0;

    /// <summary>Lê o token a partir dos 8 bytes big-endian do <c>ROWVERSION</c>.</summary>
    public static RowVersion FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8)
        {
            throw new ArgumentException("ROWVERSION deve ter exatamente 8 bytes.", nameof(bytes));
        }

        return new RowVersion(BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    /// <summary>Serializa o token nos 8 bytes big-endian esperados por um parâmetro BINARY(8).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, Value);
        return bytes;
    }
}
