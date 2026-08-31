using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Referência OPACA a uma peça de evidência de UM cenário do canário (AB-I8-004, escopo obrigatório item 6:
/// "registrar referências opacas para evidências de cada cenário, nunca SAS/token/secret/raw PII"): carrega
/// apenas um digest determinístico (<see cref="Fingerprint"/>) e um localizador curto e sanitizado
/// (<see cref="Locator"/>) — NUNCA o conteúdo bruto da evidência. Mesmo princípio e mesma validação de
/// <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessEvidenceReference"/> (que permanece
/// escopado à evidência de readiness — este tipo é o equivalente independente para o canário, não uma
/// dependência cruzada entre módulos).
/// </summary>
public sealed record CanaryEvidenceReference
{
    private const int MaxLocatorLength = 300;

    private CanaryEvidenceReference(CanaryEvidenceKind kind, Sha256Hash fingerprint, string locator)
    {
        Kind = kind;
        Fingerprint = fingerprint;
        Locator = locator;
    }

    /// <summary>Fingerprint canônico usado quando nenhuma evidência foi produzida ainda — SEMPRE um <see cref="Sha256Hash"/> concreto (nunca nulo/default).</summary>
    public static readonly Sha256Hash NoEvidenceFingerprint =
        DeterministicHash.Compute(["archivebridge.canary.no-evidence.v1"]);

    /// <summary>Instância canônica "nenhuma evidência" — usada como default fail-closed quando um cenário nunca foi observado.</summary>
    public static readonly CanaryEvidenceReference None = new(CanaryEvidenceKind.None, NoEvidenceFingerprint, locator: string.Empty);

    /// <summary>Origem da evidência.</summary>
    public CanaryEvidenceKind Kind { get; }

    /// <summary>Digest determinístico do conteúdo de evidência subjacente — nunca o conteúdo em si.</summary>
    public Sha256Hash Fingerprint { get; }

    /// <summary>Localizador curto e sanitizado (ex.: <c>"reconciliation-certificate:wave=...;job=...;v=3"</c>) — nunca segredo/PII/caminho sensível.</summary>
    public string Locator { get; }

    /// <summary>Cria uma referência a evidência resolvida automaticamente pelo agregador a partir de um store canônico existente.</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> vazio, longo demais, ou com aparência de segredo/PII.</exception>
    public static CanaryEvidenceReference SystemDerived(Sha256Hash fingerprint, string locator) =>
        new(
            CanaryEvidenceKind.SystemDerived,
            fingerprint,
            EvidenceText.RequireSafe(locator, nameof(locator), MaxLocatorLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p)));

    /// <summary>Cria uma referência a uma atestação livre de operador.</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> vazio, longo demais, ou com aparência de segredo/PII.</exception>
    public static CanaryEvidenceReference OperatorAttested(Sha256Hash fingerprint, string locator) =>
        new(
            CanaryEvidenceKind.OperatorAttestation,
            fingerprint,
            EvidenceText.RequireSafe(locator, nameof(locator), MaxLocatorLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p)));

    /// <summary>Cria a referência de evidência da decisão humana de aprovação da primeira onda real (escopo obrigatório item 11).</summary>
    /// <exception cref="ArgumentException"><paramref name="locator"/> vazio, longo demais, ou com aparência de segredo/PII.</exception>
    public static CanaryEvidenceReference ApprovalDecision(Sha256Hash fingerprint, string locator) =>
        new(
            CanaryEvidenceKind.HumanApprovalDecision,
            fingerprint,
            EvidenceText.RequireSafe(locator, nameof(locator), MaxLocatorLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p)));

    /// <summary>Reconstrói uma referência JÁ PERSISTIDA (uso exclusivo da camada de persistência) — sem revalidação de forma (o dado já passou pela validação na escrita).</summary>
    public static CanaryEvidenceReference Rehydrate(CanaryEvidenceKind kind, Sha256Hash fingerprint, string locator) =>
        new(kind, fingerprint, locator);
}
