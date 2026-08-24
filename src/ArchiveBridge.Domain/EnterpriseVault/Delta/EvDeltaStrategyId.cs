using System.Globalization;

namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Identidade opaca de UMA implementação de delta strategy (AB-4C-008 req 7): nome+versão, nunca uma
/// string livre fornecida pelo cliente — sempre resolvida pela seleção determinística
/// (<see cref="EvDeltaStrategySelectionPolicy"/>) a partir da support matrix embarcada.
/// </summary>
public readonly record struct EvDeltaStrategyId(string Name, int Version)
{
    /// <summary>Representação estável para lineage/hash/auditoria — nunca reinterpretada como identidade separada.</summary>
    public string DisplayName => $"{Name}@v{Version.ToString(CultureInfo.InvariantCulture)}";
}
