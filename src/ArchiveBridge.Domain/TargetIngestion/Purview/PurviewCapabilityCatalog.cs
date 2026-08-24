namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Fato documentado (não inventado) sobre uma rota — origem para uma <see cref="CapabilityEvidence"/>
/// recém-descoberta. <see cref="SourceReference"/>/<see cref="AsOfUtc"/> nunca são texto livre inventado em
/// tempo de execução: espelham exatamente a citação já revisada em ADR-0006/0007.
/// </summary>
public sealed record CapabilityCatalogEntry(
    CapabilityStatus Status,
    string SourceReference,
    string? DocumentationVersion,
    string? CapabilityVersionLabel,
    DateTimeOffset AsOfUtc);

/// <summary>
/// Catálogo EMBARCADO de capability por rota (espelha ADR-0006/ADR-0007, mesmo padrão de
/// <c>ConnectorSupportMatrix</c>/docs/ev/compatibility-matrix.md): consultado pela descoberta de
/// capability, NUNCA por uma chamada em tempo real ao fornecedor — Domain/Application permanecem
/// independentes de Graph/EXO/PowerShell/Purview (item 1 do work order). Divergência entre este catálogo e
/// a documentação/ADR aceito é defeito de release, mesma regra do documento fonte. Qualquer rota NÃO
/// listada aqui é <see cref="CapabilityStatus.Unknown"/> — nunca inferida como suportada
/// ("sem inventar suporte", honestidade comercial).
/// </summary>
public static class PurviewCapabilityCatalog
{
    /// <summary>Data de aceitação arquitetural do ADR-0006 (2026-07-28) — a evidência documentada da rota GA.</summary>
    private static readonly DateTimeOffset Adr0006AcceptedAtUtc = new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

    private const string PstImportSourceReference =
        "ADR-0006 — Purview Network Upload (Microsoft Learn: PST Import overview / Network upload / " +
        "Troubleshooting / FAQ — Apêndice F do runbook).";

    /// <summary>Descreve o fato documentado para a rota informada; rota desconhecida devolve <see cref="CapabilityStatus.Unknown"/>.</summary>
    public static CapabilityCatalogEntry Describe(PurviewCapabilityRoute route)
    {
        if (route.Value == PurviewCapabilityRoutes.PstImport.Value)
        {
            // GA documentado pela Microsoft e aceito arquiteturalmente pelo ADR-0006. Isto descreve o
            // status PUBLICAMENTE documentado da rota — não é, por si só, autorização de produção: Gate A
            // (validação operacional em tenant) e Gate B (contrato de implementação) do ADR-0006 permanecem
            // pendentes e são controlados separadamente (fora do escopo deste catálogo de capability).
            return new CapabilityCatalogEntry(
                CapabilityStatus.GeneralAvailability,
                PstImportSourceReference,
                DocumentationVersion: null,
                CapabilityVersionLabel: null,
                Adr0006AcceptedAtUtc);
        }

        // Honestidade comercial: nenhuma rota fora do catálogo é inferida como suportada. Sem SourceReference
        // inventada — Unknown nunca carrega uma citação, porque não há evidência documentada nenhuma.
        return new CapabilityCatalogEntry(
            CapabilityStatus.Unknown,
            SourceReference: string.Empty,
            DocumentationVersion: null,
            CapabilityVersionLabel: null,
            AsOfUtc: DateTimeOffset.MinValue);
    }
}
