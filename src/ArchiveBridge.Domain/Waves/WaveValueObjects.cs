using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Waves;

/// <summary>Versão monotônica da seleção da onda (começa em 1).</summary>
public readonly record struct WaveVersion(int Value)
{
    /// <summary>Primeira versão.</summary>
    public static WaveVersion Initial => new(1);

    /// <summary>Próxima versão (uma alteração de seleção sempre cria uma nova).</summary>
    public WaveVersion Next() => new(Value + 1);
}

/// <summary>Nome da onda (obrigatório, sem caracteres de controle).</summary>
public readonly record struct WaveName
{
    private const int MaxLength = 200;

    /// <summary>Cria um nome de onda válido.</summary>
    public WaveName(string value) => Value = TextValue.Require(value, nameof(value), MaxLength);

    /// <summary>Texto do nome.</summary>
    public string Value { get; }
}

/// <summary>
/// Referência a um archive de destino (o archive de uma mailbox). Usada para agrupar volume no
/// planejamento de capacidade — dividir artificialmente a mesma onda não contorna a regra, pois a
/// soma é por archive.
/// </summary>
public readonly record struct ArchiveRef
{
    private const int MaxLength = 320;

    /// <summary>Cria uma referência de archive válida (mailbox obrigatória).</summary>
    public ArchiveRef(string mailbox)
    {
        var normalized = TextValue.Require(mailbox, nameof(mailbox), MaxLength);
        if (normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException("Mailbox de archive não pode conter espaços.", nameof(mailbox));
        }

        Mailbox = normalized;
    }

    /// <summary>Mailbox cujo archive é o destino.</summary>
    public string Mailbox { get; }
}
