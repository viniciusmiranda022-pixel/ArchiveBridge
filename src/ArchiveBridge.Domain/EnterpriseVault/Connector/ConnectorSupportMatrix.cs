namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>
/// Nível de suporte de uma família de versão EV (docs/ev/compatibility-matrix.md) — "compatível" NUNCA
/// implica suporte comercial; apenas "certificado" habilita export capability (regra de honestidade
/// comercial, ADR-0013). <see cref="Unknown"/> é o default fail-closed quando a versão não corresponde a
/// nenhuma família conhecida — "UNKNOWN não é SUPPORTED" (docs/ev/capability-discovery.md, regra 1).
/// </summary>
public enum ConnectorSupportLevel
{
    /// <summary>Versão não reconhecida/vazia — não avaliável, nunca tratada como suportada.</summary>
    Unknown,

    /// <summary>Família identificada e explicitamente vetada.</summary>
    NotSupported,

    /// <summary>A arquitetura comporta um adapter para a família; nenhum teste executado ainda.</summary>
    Compatible,

    /// <summary>Adapter implementado e exercitado em laboratório, sem certificação completa.</summary>
    Tested,

    /// <summary>Adapter aprovado no plano de testes completo — única classificação que habilita export capability.</summary>
    Certified,
}

/// <summary>
/// Matriz de compatibilidade EV embarcada (espelha docs/ev/compatibility-matrix.md), consultada pelo
/// capability handshake do connector (AB-4C-001 critério 4). Divergência entre esta matriz e a
/// documentação publicada é defeito de release — mesma regra do documento fonte. NENHUMA família começa
/// <see cref="ConnectorSupportLevel.Certified"/>: certificação exige evidência de laboratório registrada
/// explicitamente aqui, nunca inferida da string de versão sozinha ("honestidade comercial").
/// </summary>
public static class ConnectorSupportMatrix
{
    private static readonly (string FamilyPrefix, ConnectorSupportLevel Level)[] Families =
    [
        ("15.", ConnectorSupportLevel.Compatible),
        ("14.", ConnectorSupportLevel.Compatible),
        ("13.", ConnectorSupportLevel.Compatible),
        ("12.1", ConnectorSupportLevel.Compatible),
        ("12.0", ConnectorSupportLevel.Compatible),
        ("11.", ConnectorSupportLevel.Compatible),
        ("10.", ConnectorSupportLevel.Compatible),
    ];

    /// <summary>
    /// Avalia o nível de suporte a partir da string de versão observada pelo discovery. Uma versão
    /// vazia, não reconhecida ou anterior à família mínima documentada é
    /// <see cref="ConnectorSupportLevel.Unknown"/> (fail-closed) — nunca
    /// <see cref="ConnectorSupportLevel.NotSupported"/> por omissão, pois "não suportado" é uma
    /// classificação POSITIVA (família identificada e vetada), distinta de "não avaliável".
    /// </summary>
    public static ConnectorSupportLevel Evaluate(string evVersionDisplay)
    {
        if (string.IsNullOrWhiteSpace(evVersionDisplay))
        {
            return ConnectorSupportLevel.Unknown;
        }

        var trimmed = evVersionDisplay.Trim();
        foreach (var (prefix, level) in Families)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return level;
            }
        }

        return ConnectorSupportLevel.Unknown;
    }
}
