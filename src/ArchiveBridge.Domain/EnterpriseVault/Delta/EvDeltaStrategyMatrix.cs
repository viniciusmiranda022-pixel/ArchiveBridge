namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Nível de maturidade de UMA delta strategy para uma família de versão EV (AB-4C-008 req 2/19) — espelha
/// <see cref="Connector.ConnectorSupportLevel"/> deliberadamente: "compatível" NUNCA implica suporte
/// comercial certificado; apenas <see cref="Certified"/> habilitaria delta em produção sem ressalva
/// (nenhuma família começa certificada — mesma regra de honestidade comercial do ADR-0013).
/// <see cref="Unknown"/> é o default fail-closed para versão não reconhecida — "UNKNOWN não é SUPPORTED".
/// </summary>
public enum EvDeltaStrategyCertification
{
    /// <summary>Versão não reconhecida/vazia — não avaliável, nunca tratada como elegível.</summary>
    Unknown,

    /// <summary>Família identificada e explicitamente vetada para delta.</summary>
    NotSupported,

    /// <summary>A arquitetura comporta uma strategy para a família; nenhuma evidência de laboratório ainda.</summary>
    Compatible,

    /// <summary>Strategy exercitada em laboratório, sem certificação completa do plano de testes.</summary>
    Tested,

    /// <summary>Strategy aprovada no plano de testes completo — única classificação apta a uso irrestrito.</summary>
    Certified,
}

/// <summary>
/// UMA entrada da support matrix de delta strategy: liga uma família de versão EV a uma
/// <see cref="EvDeltaStrategyId"/> concreta, o nível de maturidade e as fases que ela sabe emitir. A
/// <see cref="Precedence"/> desempata quando mais de uma entrada elegível reconhece a mesma família.
/// </summary>
public sealed record EvDeltaStrategyDescriptor(
    EvDeltaStrategyId StrategyId,
    string FamilyPrefix,
    EvDeltaStrategyCertification Certification,
    IReadOnlyList<EvDeltaPhase> SupportedPhases,
    int Precedence)
{
    /// <summary>Verdadeiro quando esta entrada é elegível para seleção (Compatible ou acima) e cobre a fase pedida.</summary>
    public bool IsEligibleFor(EvDeltaPhase phase) =>
        Certification >= EvDeltaStrategyCertification.Compatible && SupportedPhases.Contains(phase);
}

/// <summary>
/// Matriz de delta strategy embarcada (espelha docs/ev/compatibility-matrix.md, seção delta) — divergência
/// entre esta matriz e a documentação publicada é defeito de release. Reaproveita as MESMAS famílias de
/// versão de <see cref="Connector.ConnectorSupportMatrix"/>: uma versão desconhecida para o handshake de
/// export é igualmente desconhecida aqui — nunca uma matriz paralela e divergente.
/// </summary>
public static class EvDeltaStrategyCatalog
{
    private static readonly EvDeltaStrategyId CompositeWatermarkV1 = new("EV-COMPOSITE-WATERMARK", 1);

    private static readonly IReadOnlyList<EvDeltaPhase> AllPhases =
        [EvDeltaPhase.Baseline, EvDeltaPhase.Delta, EvDeltaPhase.FinalDelta];

    private static readonly EvDeltaStrategyDescriptor[] Descriptors =
    [
        new(CompositeWatermarkV1, "15.", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
        new(CompositeWatermarkV1, "14.", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
        new(CompositeWatermarkV1, "13.", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
        new(CompositeWatermarkV1, "12.1", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
        new(CompositeWatermarkV1, "12.0", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
        new(CompositeWatermarkV1, "11.", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
        new(CompositeWatermarkV1, "10.", EvDeltaStrategyCertification.Compatible, AllPhases, Precedence: 10),
    ];

    /// <summary>
    /// Devolve as entradas cuja família corresponde à versão observada. Vazio ⇒ família desconhecida
    /// (fail-closed — nunca inferida). Uma versão vazia/em branco nunca corresponde a nenhuma família.
    /// </summary>
    public static IReadOnlyList<EvDeltaStrategyDescriptor> Evaluate(string evVersionDisplay)
    {
        if (string.IsNullOrWhiteSpace(evVersionDisplay))
        {
            return [];
        }

        var trimmed = evVersionDisplay.Trim();
        return Descriptors.Where(descriptor => trimmed.StartsWith(descriptor.FamilyPrefix, StringComparison.Ordinal)).ToArray();
    }
}
