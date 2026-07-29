namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Token de fencing do lease (ADR-0003). Incrementa a cada reivindicação; um worker que
/// perdeu o lease carrega uma época antiga e é rejeitado. NÃO confundir com <c>rowversion</c>
/// (controle de concorrência otimista) — o fencing token é a <see cref="LeaseEpoch"/>.
/// </summary>
public readonly record struct LeaseEpoch(long Value)
{
    /// <summary>Época inicial de um Job recém-criado, ainda não reivindicado.</summary>
    public static LeaseEpoch Initial => new(0);

    /// <summary>Próxima época, atribuída no momento da reivindicação.</summary>
    public LeaseEpoch Next() => new(Value + 1);
}
