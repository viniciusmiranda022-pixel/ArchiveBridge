using System.Text.Json;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Serializa a evidência de descoberta em JSON CANÔNICO e determinístico: apenas o conteúdo semântico —
/// identidade do ambiente, capacidades ordenadas, assinatura, a SELEÇÃO completa (desfecho, candidatos,
/// selecionado) e CADA avaliação de adapter por inteiro (compatibilidade, precedência, perfil, maturidade,
/// requisitos, capacidades declaradas e achados, todos ordenados), os achados com <see cref="EvErrorCategory"/>
/// e as impressões digitais AUTORITATIVAS da reserva — SEM <c>run_id</c> nem timestamps voláteis (que ficam
/// nos metadados SQL). Um auditor consegue determinar POR QUE um adapter foi selecionado a partir só deste
/// artefato, sem depender das tabelas SQL. O mesmo resultado semântico produz bytes byte-a-byte idênticos
/// (ordenação ordinal explícita), viabilizando republicação idempotente e reconciliação; alterar um único
/// campo muda os bytes e o hash.
/// </summary>
public sealed class EvDiscoveryEvidenceSerializer : IEvDiscoveryEvidenceSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <inheritdoc />
    public EvDiscoveryEvidenceBytes Serialize(
        EvDiscoveryRunResult result, Sha256Hash configurationHash, Sha256Hash semanticEvidenceHash)
    {
        ArgumentNullException.ThrowIfNull(result);
        var document = new EvidenceDocument(
            EvDiscoverySchema.Version,
            // Impressões digitais AUTORITATIVAS da reserva (completas), registradas explicitamente.
            new ReservationFingerprintsDocument(configurationHash.Value, semanticEvidenceHash.Value),
            new EnvironmentDocument(
                result.Environment.EnvironmentId.Value,
                result.Environment.SiteName,
                result.Environment.DirectoryServer,
                result.Environment.ObservedVersion,
                result.Environment.ProductVersion,
                result.Environment.DiscoverySource),
            result.CapabilitySet.AdapterId?.Value,
            result.CapabilitySet.AdapterVersion,
            result.Selection.Outcome.ToString(),
            [.. EvDiscoveryCanonical.OrderCandidates(result.Selection.Candidates)],
            result.Status.ToString(),
            result.ResultCode.Value,
            OrderCapabilities(result.CapabilitySet.Capabilities),
            result.Signature is { } signature
                ? new SignatureDocument(
                    signature.CommandName,
                    signature.ModuleSource,
                    signature.ObservedVersion,
                    signature.CommandType,
                    signature.Parameters,
                    signature.MandatoryParameters,
                    signature.ParameterSets,
                    signature.SignatureHash.Value)
                : null,
            // Avaliações de adapter COMPLETAS em ORDEM CANÔNICA TOTAL (mesma regra do fingerprint).
            [.. EvDiscoveryCanonical.OrderEvaluations(result.Selection.Evaluations).Select(ToEvaluationDocument)],
            OrderFindings(result.Selection.Findings),
            OrderFindings(result.BlockingFindings),
            // Hashes INTERNOS do conjunto de capacidades — NÃO são as impressões digitais completas da
            // reserva (essas ficam em ReservationFingerprints, acima).
            result.CapabilitySet.ConfigurationHash.Value,
            result.CapabilitySet.EvidenceHash.Value);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        return new EvDiscoveryEvidenceBytes(bytes, DeterministicHash.ComputeBytes(bytes));
    }

    private static EvaluationDocument ToEvaluationDocument(EvAdapterEvaluation evaluation) =>
        new(
            evaluation.AdapterId.Value,
            evaluation.AdapterVersion,
            evaluation.Compatibility.ToString(),
            evaluation.Precedence,
            evaluation.ProfileId,
            evaluation.Maturity is { } maturity
                ? new MaturityDocument(
                    maturity.RuntimeObserved, maturity.OfficialDocumentation, maturity.AutomatedFixtureValidated, maturity.LaboratoryValidated)
                : null,
            [.. EvDiscoveryCanonical.OrderRequirements(evaluation.Requirements)
                .Select(static r => new RequirementDocument(r.CapabilityCode.Value, r.Description))],
            OrderCapabilities(evaluation.Capabilities),
            OrderFindings(evaluation.Findings));

    // Delegam à canonicalização ÚNICA do domínio (ordem TOTAL) — mesma regra do EvDiscoverySemanticFingerprint.
    private static CapabilityDocument[] OrderCapabilities(IEnumerable<EvCapability> capabilities) =>
        [.. EvDiscoveryCanonical.OrderCapabilities(capabilities)
            .Select(static c => new CapabilityDocument(
                c.CapabilityCode.Value, c.CapabilityVersion, c.Availability.ToString(), c.EvidenceReference, c.BlockingReason))];

    private static FindingDocument[] OrderFindings(IEnumerable<EvDiscoveryFinding> findings) =>
        [.. EvDiscoveryCanonical.OrderFindings(findings)
            .Select(static f => new FindingDocument(
                f.ResultCode.Value, f.CapabilityCode?.Value, f.Reason, f.ErrorCategory.ToString()))];

    private sealed record EvidenceDocument(
        int SchemaVersion,
        ReservationFingerprintsDocument ReservationFingerprints,
        EnvironmentDocument Environment,
        string? SelectedAdapter,
        int? AdapterVersion,
        string SelectionOutcome,
        IReadOnlyList<string> AdapterCandidates,
        string Status,
        string ResultCode,
        IReadOnlyList<CapabilityDocument> Capabilities,
        SignatureDocument? Signature,
        IReadOnlyList<EvaluationDocument> AdapterEvaluations,
        IReadOnlyList<FindingDocument> SelectionFindings,
        IReadOnlyList<FindingDocument> Findings,
        string CapabilitySetConfigurationHash,
        string CapabilitySetEvidenceHash);

    /// <summary>Impressões digitais AUTORITATIVAS usadas pela reserva (configuração completa + evidência semântica completa).</summary>
    private sealed record ReservationFingerprintsDocument(string ConfigurationHash, string SemanticEvidenceHash);

    private sealed record EnvironmentDocument(
        Guid EnvironmentId, string SiteName, string DirectoryServer, string ObservedVersion, string ProductVersion, string DiscoverySource);

    private sealed record CapabilityDocument(
        string CapabilityCode, int CapabilityVersion, string Availability, string EvidenceReference, string? BlockingReason);

    private sealed record SignatureDocument(
        string CommandName, string ModuleSource, string ObservedVersion, string CommandType,
        IReadOnlyList<string> Parameters, IReadOnlyList<string> MandatoryParameters, IReadOnlyList<string> ParameterSets, string SignatureHash);

    /// <summary>Avaliação de adapter COMPLETA registrada na evidência (sustenta a seleção sem depender do SQL).</summary>
    private sealed record EvaluationDocument(
        string AdapterId,
        int AdapterVersion,
        string Compatibility,
        int Precedence,
        string? ProfileId,
        MaturityDocument? Maturity,
        IReadOnlyList<RequirementDocument> Requirements,
        IReadOnlyList<CapabilityDocument> DeclaredCapabilities,
        IReadOnlyList<FindingDocument> Findings);

    /// <summary>Maturidade registrada SEPARADAMENTE; <c>LaboratoryValidated</c> nunca é true sem laboratório.</summary>
    private sealed record MaturityDocument(
        bool RuntimeObserved, bool OfficialDocumentation, bool AutomatedFixtureValidated, bool LaboratoryValidated);

    private sealed record RequirementDocument(string CapabilityCode, string Description);

    private sealed record FindingDocument(string ResultCode, string? CapabilityCode, string Reason, string ErrorCategory);
}
