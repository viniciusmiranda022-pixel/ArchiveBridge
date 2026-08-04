using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>
/// Base de um adapter de versão POR CAPACIDADES: interpreta a observação factual e a assinatura observada
/// do cmdlet de exportação contra a FORMA que este adapter reconhece — nunca pela versão textual. Declara
/// compatibilidade (Supported quando reconhece a assinatura; Blocked quando reconhece o EV mas o cmdlet não
/// está disponível; Unsupported caso contrário) e compõe o conjunto de capacidades = fatos observados +
/// julgamentos de suporte (assinatura/PST/tamanho/relatório/filtragem/versão). Nunca executa exportação.
/// </summary>
public abstract class EvVersionAdapterBase : IEvVersionAdapter
{
    /// <inheritdoc />
    public abstract EvAdapterId AdapterId { get; }

    /// <inheritdoc />
    public abstract int AdapterVersion { get; }

    /// <summary>Precedência determinística de desempate (maior vence). Empate entre compatíveis ⇒ ambíguo.</summary>
    protected abstract int Precedence { get; }

    /// <summary>Parâmetro de saída que ESTA forma de assinatura exige (ex.: <c>OutputPath</c>/<c>ExportPath</c>).</summary>
    protected abstract string OutputParameter { get; }

    /// <inheritdoc />
    public EvAdapterEvaluation Evaluate(EvDiscoveryObservation observation, EvDiscoveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(policy);
        var now = observation.Environment.DiscoveredAtUtc;
        var cmdletAvailable = observation.AvailabilityOf(EvCapabilityCodes.EvExportCmdletAvailable) == CapabilityAvailability.Available;
        var recognizesEv = observation.AvailabilityOf(EvCapabilityCodes.EvModuleAvailable) == CapabilityAvailability.Available
            || observation.AvailabilityOf(EvCapabilityCodes.EvSnapinAvailable) == CapabilityAvailability.Available;
        var signature = observation.ExportSignature;
        var recognizesSignature = cmdletAvailable && signature is not null && RecognizesSignature(signature);

        var requirements = new[]
        {
            new EvAdapterRequirement(new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletAvailable), "cmdlet de exportação presente"),
            new EvAdapterRequirement(new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletSignatureSupported), $"assinatura com {OutputParameter} e ArchiveId"),
        };

        if (recognizesSignature)
        {
            var capabilities = BuildCapabilities(observation, signature!, supported: true, now);
            return new EvAdapterEvaluation(AdapterId, AdapterVersion, AdapterCompatibility.Supported, capabilities, requirements, [], Precedence);
        }

        var compatibility = recognizesEv ? AdapterCompatibility.Blocked : AdapterCompatibility.Unsupported;
        var resultCode = cmdletAvailable ? EvDiscoveryResultCodes.CmdletSignatureUnsupported : EvDiscoveryResultCodes.CmdletNotAvailable;
        var findings = new[]
        {
            new EvDiscoveryFinding(new EvDiscoveryResultCode(resultCode),
                new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletSignatureSupported),
                $"Adapter {AdapterId} não reconhece a assinatura observada.", now),
        };
        var unsupportedCapabilities = BuildCapabilities(observation, signature, supported: false, now);
        return new EvAdapterEvaluation(AdapterId, AdapterVersion, compatibility, unsupportedCapabilities, requirements, findings, Precedence);
    }

    private bool RecognizesSignature(EvExportSignature signature) =>
        signature.HasParameter("ArchiveId") && signature.HasParameter(OutputParameter);

    private EvCapability[] BuildCapabilities(EvDiscoveryObservation observation, EvExportSignature? signature, bool supported, DateTimeOffset now)
    {
        var capabilities = observation.ProbeResults
            .Select(probe => new EvCapability(probe.CapabilityCode, 1, probe.Availability, probe.EvidenceReference, probe.BlockingReason, now))
            .ToList();

        capabilities.Add(Judge(EvCapabilityCodes.EvExportCmdletSignatureSupported, supported, now));
        capabilities.Add(Judge(EvCapabilityCodes.EvVersionSupportedByAdapter, supported, now));
        capabilities.Add(Judge(EvCapabilityCodes.EvExportPstSupported, supported && signature is not null && signature.HasParameter(OutputParameter), now));
        capabilities.Add(Judge(EvCapabilityCodes.EvExportSizeParameterSupported, supported && signature is not null && signature.HasParameter("MaxSizeMB"), now));
        capabilities.Add(Judge(EvCapabilityCodes.EvExportReportSupported, supported && signature is not null && signature.HasParameter("GenerateReport"), now));
        capabilities.Add(Judge(EvCapabilityCodes.EvExportFilteringSupported, supported && signature is not null && signature.HasParameter("Filter"), now));
        return [.. capabilities];
    }

    private EvCapability Judge(string code, bool available, DateTimeOffset now) =>
        new(new EvCapabilityCode(code), 1,
            available ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
            $"adapter:{AdapterId.Value}:{code}",
            available ? null : "Não suportado pela assinatura observada.",
            now);
}

/// <summary>
/// Adapter para a forma MODERNA da assinatura de exportação (parâmetro de saída <c>OutputPath</c>, com
/// suporte a tamanho/relatório/filtragem quando presentes). Precedência maior que a forma legada.
/// </summary>
public sealed class EvExportModernAdapter : EvVersionAdapterBase
{
    /// <inheritdoc />
    public override EvAdapterId AdapterId { get; } = new("ev-export-adapter-modern");

    /// <inheritdoc />
    public override int AdapterVersion => 1;

    /// <inheritdoc />
    protected override int Precedence => 20;

    /// <inheritdoc />
    protected override string OutputParameter => "OutputPath";
}

/// <summary>
/// Adapter para a forma LEGADA da assinatura de exportação (parâmetro de saída <c>ExportPath</c>). Só
/// reconhece assinaturas antigas; nunca alega suporte universal a todas as versões do Enterprise Vault.
/// </summary>
public sealed class EvExportLegacyAdapter : EvVersionAdapterBase
{
    /// <inheritdoc />
    public override EvAdapterId AdapterId { get; } = new("ev-export-adapter-legacy");

    /// <inheritdoc />
    public override int AdapterVersion => 1;

    /// <inheritdoc />
    protected override int Precedence => 10;

    /// <inheritdoc />
    protected override string OutputParameter => "ExportPath";
}
