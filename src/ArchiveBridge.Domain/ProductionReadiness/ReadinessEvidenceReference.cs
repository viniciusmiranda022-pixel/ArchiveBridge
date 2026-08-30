using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Referência OPACA a uma peça de evidência (AB-I8-001, escopo obrigatório item 1: "evidence references
/// opacas"): carrega apenas um digest determinístico (<see cref="Fingerprint"/>) e um localizador curto e
/// sanitizado (<see cref="Locator"/>) — NUNCA o conteúdo bruto da evidência, NUNCA segredo/SAS/token/caminho
/// físico sensível/PII (STOP-THE-LINE do work order). <see cref="Locator"/> é validado com o mesmo guarda
/// fail-closed de <see cref="EvidenceText"/> já usado por AB-I7-008 — qualquer valor com aparência de
/// segredo/PII é recusado na fronteira, nunca redigido silenciosamente.
/// </summary>
public sealed record ReadinessEvidenceReference
{
    private const int MaxLocatorLength = 300;

    private ReadinessEvidenceReference(ReadinessEvidenceKind kind, Sha256Hash fingerprint, string locator)
    {
        Kind = kind;
        Fingerprint = fingerprint;
        Locator = locator;
    }

    /// <summary>Fingerprint canônico usado quando nenhuma evidência foi produzida ainda — SEMPRE um <see cref="Sha256Hash"/> concreto (nunca nulo/default).</summary>
    public static readonly Sha256Hash NoEvidenceFingerprint =
        DeterministicHash.Compute(["archivebridge.production-readiness.no-evidence.v1"]);

    /// <summary>Instância canônica "nenhuma evidência" — usada como default fail-closed quando um controle nunca foi observado.</summary>
    public static readonly ReadinessEvidenceReference None = new(ReadinessEvidenceKind.None, NoEvidenceFingerprint, locator: string.Empty);

    /// <summary>Origem da evidência.</summary>
    public ReadinessEvidenceKind Kind { get; }

    /// <summary>Digest determinístico do conteúdo de evidência subjacente — nunca o conteúdo em si.</summary>
    public Sha256Hash Fingerprint { get; }

    /// <summary>Localizador curto e sanitizado (ex.: <c>"recovery-readiness:RestoreDrill:v12"</c>) — nunca segredo/PII/caminho sensível.</summary>
    public string Locator { get; }

    /// <summary>Cria uma referência a evidência resolvida automaticamente pelo agregador a partir de um store canônico existente.</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> vazio, longo demais, ou com aparência de segredo/PII.</exception>
    public static ReadinessEvidenceReference SystemDerived(Sha256Hash fingerprint, string locator) =>
        new(
            ReadinessEvidenceKind.SystemDerived,
            fingerprint,
            EvidenceText.RequireSafe(locator, nameof(locator), MaxLocatorLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p)));

    /// <summary>Cria uma referência a uma atestação manual RBAC'd.</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> vazio, longo demais, ou com aparência de segredo/PII.</exception>
    public static ReadinessEvidenceReference Attested(Sha256Hash fingerprint, string locator) =>
        new(
            ReadinessEvidenceKind.ManualAttestation,
            fingerprint,
            EvidenceText.RequireSafe(locator, nameof(locator), MaxLocatorLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p)));

    /// <summary>Reconstrói uma referência JÁ PERSISTIDA (uso exclusivo da camada de persistência) — sem revalidação de forma (o dado já passou pela validação na escrita).</summary>
    public static ReadinessEvidenceReference Rehydrate(ReadinessEvidenceKind kind, Sha256Hash fingerprint, string locator) =>
        new(kind, fingerprint, locator);
}
