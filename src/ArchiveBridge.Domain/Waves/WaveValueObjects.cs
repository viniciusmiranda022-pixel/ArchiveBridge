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
/// Referência a um archive de destino. Carrega DUAS coisas distintas: a <see cref="Identity"/>
/// canônica (chave de agrupamento do planejamento de capacidade — a soma é por identidade, não pela
/// string de exibição, então variação de caixa/alias não divide artificialmente o volume) e a
/// <see cref="Mailbox"/> de exibição (metadado usado na coluna <c>Mailbox</c> do CSV, com a caixa
/// preservada). Dividir artificialmente a mesma onda não contorna a regra, pois a soma é por
/// <see cref="Identity"/>.
/// </summary>
public readonly record struct ArchiveRef
{
    private const int MaxLength = 320;

    /// <summary>Cria uma referência derivando a identidade canônica da própria mailbox (só caixa).</summary>
    public ArchiveRef(string mailbox)
    {
        Mailbox = Normalize(mailbox);
        Identity = TargetArchiveId.FromMailbox(Mailbox);
    }

    /// <summary>Cria uma referência com a identidade canônica resolvida explicitamente (unifica aliases).</summary>
    public ArchiveRef(string mailbox, TargetArchiveId identity)
    {
        Mailbox = Normalize(mailbox);
        Identity = identity;
    }

    /// <summary>Mailbox de exibição (preserva a caixa; usada na coluna Mailbox do CSV).</summary>
    public string Mailbox { get; }

    /// <summary>Identidade canônica do archive (chave de agrupamento de capacidade).</summary>
    public TargetArchiveId Identity { get; }

    private static string Normalize(string mailbox)
    {
        var normalized = TextValue.Require(mailbox, nameof(mailbox), MaxLength);
        if (normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException("Mailbox de archive não pode conter espaços.", nameof(mailbox));
        }

        return normalized;
    }
}
