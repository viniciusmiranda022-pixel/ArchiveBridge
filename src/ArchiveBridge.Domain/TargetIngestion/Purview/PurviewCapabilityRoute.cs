using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Identificador estável de uma ROTA de capability do adapter Purview (nunca do provedor inteiro — ADR-0007
/// "capability específica, não global"). Texto sanitizado e limitado; a rota é a chave de escopo da
/// <see cref="CapabilityEvidence"/> persistida.
/// </summary>
public readonly record struct PurviewCapabilityRoute
{
    private const int MaxLength = 200;

    /// <summary>Cria uma rota a partir de um identificador já conhecido/documentado.</summary>
    public PurviewCapabilityRoute(string value) => Value = TextValue.Require(value, nameof(value), MaxLength);

    /// <summary>Identificador textual estável da rota.</summary>
    public string Value { get; }
}

/// <summary>Rotas de capability Purview conhecidas por este Passo (I5/EPIC-06 Passo 1).</summary>
public static class PurviewCapabilityRoutes
{
    /// <summary>
    /// Importação de PST via Purview Network Upload (ADR-0006) — cobre tanto mailbox primária quanto
    /// Online Archive; o precheck de archive (§25.2-§25.4) aplica preconditions adicionais sobre a MESMA
    /// rota GA, não uma rota de capability distinta.
    /// </summary>
    public static PurviewCapabilityRoute PstImport { get; } = new("Purview.NetworkUpload.PstImport");
}
