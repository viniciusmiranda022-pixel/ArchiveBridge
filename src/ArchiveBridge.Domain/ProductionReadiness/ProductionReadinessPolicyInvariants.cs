using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Auto-checagem PURA e determinística (sem I/O, nunca chama host/tenant real) dos dois controles §47.5
/// que já são invariantes de CÓDIGO vivo neste repositório (AB-I8-001) — ao invés de exigir atestação
/// manual para algo que o próprio domínio já impõe em runtime, este tipo EXERCITA a regra real e reporta o
/// resultado observado. Isso é evidência genuína (não fabricada): se algum incremento futuro afrouxar o
/// limite ou a rejeição de root, esta checagem detecta e o gate falha fechado — nunca compara a constante
/// contra si mesma (o que seria tautológico).
/// </summary>
public static class ProductionReadinessPolicyInvariants
{
    /// <summary>Limite de import por archive documentado pelo runbook §25.4/§27 (100 GB) — valor LITERAL, não derivado de <see cref="PurviewPolicyLimits"/>.</summary>
    private const long DocumentedMainArchiveImportLimitBytes = 100_000_000_000L;

    /// <summary>Limite de linhas de dados do CSV mapping documentado pelo runbook §25.8 (500 linhas) — valor LITERAL.</summary>
    private const int DocumentedMaxCsvDataRows = 500;

    /// <summary>Resolve os dois controles Microsoft365 <see cref="ReadinessControlEvidenceSource.SystemDerived"/> por auto-checagem.</summary>
    public static IReadOnlyList<ReadinessControlResult> Evaluate(DateTimeOffset observedAtUtc) =>
    [
        EvaluateImportLimits(observedAtUtc),
        EvaluateTargetRootPolicy(observedAtUtc),
    ];

    private static ReadinessControlResult EvaluateImportLimits(DateTimeOffset observedAtUtc)
    {
        var controlId = new ReadinessControlId("M365.IMPORT_LIMITS_100GB_500ROWS");
        var limits = PurviewPolicyLimits.RunbookDefault;
        var matches = limits.MainArchiveImportLimitBytes == DocumentedMainArchiveImportLimitBytes
            && limits.MaxCsvDataRows == DocumentedMaxCsvDataRows;

        var fingerprint = DeterministicHash.Compute(
        [
            "archivebridge.production-readiness.policy-invariant.import-limits.v1",
            limits.MainArchiveImportLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limits.MaxCsvDataRows.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);

        return matches
            ? ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(fingerprint, "policy-invariant:PurviewPolicyLimits.RunbookDefault"),
                reasonCode: string.Empty, observedAtUtc)
            : ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Fail,
                ReadinessEvidenceReference.SystemDerived(fingerprint, "policy-invariant:PurviewPolicyLimits.RunbookDefault"),
                reasonCode: "IMPORT_LIMITS_DRIFTED_FROM_RUNBOOK", observedAtUtc);
    }

    private static ReadinessControlResult EvaluateTargetRootPolicy(DateTimeOffset observedAtUtc)
    {
        var controlId = new ReadinessControlId("M365.TARGET_ROOT_POLICY");

        var rootIsRejected = false;
        try
        {
            _ = new TargetRootFolder("/");
        }
        catch (ArgumentException)
        {
            rootIsRejected = true;
        }

        var fingerprint = DeterministicHash.Compute(
        [
            "archivebridge.production-readiness.policy-invariant.target-root-policy.v1",
            rootIsRejected ? "rejected" : "accepted",
        ]);

        return rootIsRejected
            ? ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(fingerprint, "policy-invariant:TargetRootFolder(\"/\")"),
                reasonCode: string.Empty, observedAtUtc)
            : ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Fail,
                ReadinessEvidenceReference.SystemDerived(fingerprint, "policy-invariant:TargetRootFolder(\"/\")"),
                reasonCode: "TARGET_ROOT_POLICY_NO_LONGER_REJECTS_ROOT", observedAtUtc);
    }
}
