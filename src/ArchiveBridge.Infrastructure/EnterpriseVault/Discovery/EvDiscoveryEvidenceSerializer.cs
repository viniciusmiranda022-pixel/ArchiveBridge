using System.Text.Json;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Serializa a evidência de descoberta em JSON CANÔNICO e determinístico: apenas o conteúdo semântico
/// (identidade do ambiente, adapter, capacidades ordenadas, assinatura, achados, hashes) — SEM
/// <c>run_id</c> nem timestamps voláteis, que ficam nos metadados SQL. O mesmo resultado semântico produz
/// bytes byte-a-byte idênticos, viabilizando republicação idempotente e reconciliação; alterar um único
/// campo de capacidade muda os bytes e o hash.
/// </summary>
public sealed class EvDiscoveryEvidenceSerializer : IEvDiscoveryEvidenceSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <inheritdoc />
    public EvDiscoveryEvidenceBytes Serialize(EvDiscoveryRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var document = new EvidenceDocument(
            EvDiscoverySchema.Version,
            new EnvironmentDocument(
                result.Environment.EnvironmentId.Value,
                result.Environment.SiteName,
                result.Environment.DirectoryServer,
                result.Environment.ObservedVersion,
                result.Environment.ProductVersion,
                result.Environment.DiscoverySource),
            result.CapabilitySet.AdapterId?.Value,
            result.CapabilitySet.AdapterVersion,
            result.Selection.Candidates
                .Select(static candidate => candidate.Value)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            result.Status.ToString(),
            result.ResultCode.Value,
            result.CapabilitySet.Capabilities
                .OrderBy(static capability => capability.CapabilityCode.Value, StringComparer.Ordinal)
                .Select(static capability => new CapabilityDocument(
                    capability.CapabilityCode.Value,
                    capability.CapabilityVersion,
                    capability.Availability.ToString(),
                    capability.EvidenceReference,
                    capability.BlockingReason))
                .ToArray(),
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
            result.BlockingFindings
                .Select(static finding => new FindingDocument(
                    finding.ResultCode.Value, finding.CapabilityCode?.Value, finding.Reason))
                .ToArray(),
            result.CapabilitySet.ConfigurationHash.Value,
            result.CapabilitySet.EvidenceHash.Value);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        return new EvDiscoveryEvidenceBytes(bytes, DeterministicHash.ComputeBytes(bytes));
    }

    private sealed record EvidenceDocument(
        int SchemaVersion,
        EnvironmentDocument Environment,
        string? SelectedAdapter,
        int? AdapterVersion,
        IReadOnlyList<string> AdapterCandidates,
        string Status,
        string ResultCode,
        IReadOnlyList<CapabilityDocument> Capabilities,
        SignatureDocument? Signature,
        IReadOnlyList<FindingDocument> Findings,
        string ConfigurationHash,
        string EvidenceHash);

    private sealed record EnvironmentDocument(
        Guid EnvironmentId, string SiteName, string DirectoryServer, string ObservedVersion, string ProductVersion, string DiscoverySource);

    private sealed record CapabilityDocument(
        string CapabilityCode, int CapabilityVersion, string Availability, string EvidenceReference, string? BlockingReason);

    private sealed record SignatureDocument(
        string CommandName, string ModuleSource, string ObservedVersion, string CommandType,
        IReadOnlyList<string> Parameters, IReadOnlyList<string> MandatoryParameters, IReadOnlyList<string> ParameterSets, string SignatureHash);

    private sealed record FindingDocument(string ResultCode, string? CapabilityCode, string Reason);
}
