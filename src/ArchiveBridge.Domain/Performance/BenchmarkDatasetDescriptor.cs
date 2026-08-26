using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Performance;

/// <summary>
/// Descreve um dataset SINTÉTICO usado por um cenário de benchmark — nunca um caminho real, nome de
/// mailbox ou qualquer identificador que aponte para dado de cliente (AB-I7-003 §7/§9: sem PII/segredo nos
/// resultados). <see cref="Name"/> é validado para recusar barras/backslash/arroba, que tipicamente
/// aparecem em caminho de arquivo ou endereço de e-mail — defesa em profundidade além da disciplina de só
/// gerar datasets sintéticos no chamador.
/// </summary>
public sealed record BenchmarkDatasetDescriptor
{
    /// <summary>Cria o descritor, validando forma do rótulo e não-negatividade dos tamanhos.</summary>
    /// <exception cref="ArgumentException">
    /// Nome vazio, longo demais, com caractere de controle, ou contendo separador de caminho/arroba
    /// (indício de que não é um rótulo sintético).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeBytes"/> ou <paramref name="itemCount"/> negativos.</exception>
    public BenchmarkDatasetDescriptor(string name, long sizeBytes, int itemCount, int seed)
    {
        var trimmed = TextValue.Require(name, nameof(name), 200);
        if (trimmed.IndexOfAny(['/', '\\', '@']) >= 0)
        {
            throw new ArgumentException(
                "O rótulo do dataset não pode conter '/', '\\' ou '@' — indício de caminho real/endereço em vez de um rótulo sintético.",
                nameof(name));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "O tamanho do dataset não pode ser negativo.");
        }

        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), "A contagem de itens não pode ser negativa.");
        }

        Name = trimmed;
        SizeBytes = sizeBytes;
        ItemCount = itemCount;
        Seed = seed;
    }

    /// <summary>Rótulo sintético do dataset (ex.: <c>synthetic-small-4KiB</c>).</summary>
    public string Name { get; }

    /// <summary>Tamanho total em bytes (0 quando não aplicável ao cenário).</summary>
    public long SizeBytes { get; }

    /// <summary>Número de itens (linhas/arquivos/mensagens sintéticas) do dataset.</summary>
    public int ItemCount { get; }

    /// <summary>Seed determinística usada para gerar o conteúdo sintético (reprodutibilidade).</summary>
    public int Seed { get; }
}
