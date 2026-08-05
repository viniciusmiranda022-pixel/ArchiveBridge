using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>
/// Adapter de versão POR CAPACIDADES para um <see cref="EvExportProfile"/> documentado: interpreta a
/// observação factual e a assinatura observada do <c>Export-EVArchive</c> contra a FORMA documentada do
/// perfil — nunca pela versão textual. Declara compatibilidade (Supported quando reconhece a assinatura
/// identificadora; Blocked quando reconhece o EV mas o cmdlet não está disponível; Unsupported caso
/// contrário) e compõe capacidades = fatos observados + julgamentos de suporte. O suporte a RELATÓRIO
/// NÃO depende de parâmetro (o relatório é gerado automaticamente). Registra o perfil e sua maturidade
/// (runtime observado + documentação oficial; nunca laboratório validado). Nunca executa exportação.
/// </summary>
public abstract class EvVersionAdapterBase(EvExportProfile profile) : IEvVersionAdapter
{
    private readonly EvExportProfile _profile = profile;

    /// <inheritdoc />
    public abstract EvAdapterId AdapterId { get; }

    /// <inheritdoc />
    public abstract int AdapterVersion { get; }

    /// <summary>Precedência determinística de desempate (maior vence). Empate entre compatíveis ⇒ ambíguo.</summary>
    protected abstract int Precedence { get; }

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
            new EvAdapterRequirement(new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletSignatureSupported),
                $"assinatura documentada ({string.Join(" + ", _profile.IdentifyingParameters)})"),
        };

        if (recognizesSignature)
        {
            var capabilities = BuildCapabilities(observation, signature!, supported: true, now);
            var maturity = _profile.DocumentedMaturity with { RuntimeObserved = true };
            return new EvAdapterEvaluation(
                AdapterId, AdapterVersion, AdapterCompatibility.Supported, capabilities, requirements, [], Precedence, _profile.ProfileId, maturity);
        }

        var compatibility = recognizesEv ? AdapterCompatibility.Blocked : AdapterCompatibility.Unsupported;
        var resultCode = cmdletAvailable ? EvDiscoveryResultCodes.CmdletSignatureUnsupported : EvDiscoveryResultCodes.CmdletNotAvailable;
        var findings = new[]
        {
            new EvDiscoveryFinding(new EvDiscoveryResultCode(resultCode),
                new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletSignatureSupported),
                $"Adapter {AdapterId} não reconhece a assinatura documentada {_profile.ProfileId}.", now),
        };
        var unsupportedCapabilities = BuildCapabilities(observation, signature, supported: false, now);
        return new EvAdapterEvaluation(
            AdapterId, AdapterVersion, compatibility, unsupportedCapabilities, requirements, findings, Precedence,
            _profile.ProfileId, _profile.DocumentedMaturity);
    }

    // Reconhece a assinatura pela forma IDENTIFICADORA do perfil (todos os parâmetros identificadores).
    private bool RecognizesSignature(EvExportSignature signature) =>
        _profile.IdentifyingParameters.All(signature.HasParameter);

    private EvCapability[] BuildCapabilities(EvDiscoveryObservation observation, EvExportSignature? signature, bool supported, DateTimeOffset now)
    {
        var capabilities = observation.ProbeResults
            .Select(probe => new EvCapability(probe.CapabilityCode, 1, probe.Availability, probe.EvidenceReference, probe.BlockingReason, now))
            .ToList();

        var hasFormat = signature is not null && signature.HasParameter(EvExportParameters.Format);
        capabilities.Add(Judge(EvCapabilityCodes.EvExportCmdletSignatureSupported, supported, now));
        capabilities.Add(Judge(EvCapabilityCodes.EvVersionSupportedByAdapter, supported, now));
        // Format documentado com suporte a PST ⇒ exportação PST suportada.
        capabilities.Add(Judge(EvCapabilityCodes.EvExportPstSupported, supported && hasFormat, now));
        capabilities.Add(Judge(EvCapabilityCodes.EvExportSizeParameterSupported, supported && signature is not null && signature.HasParameter(EvExportParameters.MaxPstSizeMb), now));
        // Relatório é gerado AUTOMATICAMENTE (não depende de parâmetro): suportado quando a exportação é suportada.
        capabilities.Add(Judge(EvCapabilityCodes.EvExportReportSupported, supported, now));
        capabilities.Add(Judge(EvCapabilityCodes.EvExportFilteringSupported, supported && signature is not null && signature.HasParameter(EvExportParameters.SearchString), now));
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
/// Adapter do perfil OFICIAL DOCUMENTADO do Enterprise Vault 15.1 (<c>EV_EXPORT_PROFILE_DOCUMENTED_15_1</c>):
/// reconhece a assinatura documentada do <c>Export-EVArchive</c> (<c>ArchiveId</c> + <c>OutputDirectory</c> +
/// <c>Format</c>, com <c>MaxPSTSizeMB</c>/<c>SearchString</c>/<c>MaxThreads</c>/<c>Retry</c>). Maturidade:
/// documentação oficial, **sem** homologação em laboratório. Não há adapter genérico universal.
/// </summary>
public sealed class EvExportDocumented151Adapter() : EvVersionAdapterBase(EvExportProfile.Documented151)
{
    /// <inheritdoc />
    public override EvAdapterId AdapterId { get; } = new("ev-export-adapter-documented-15-1");

    /// <inheritdoc />
    public override int AdapterVersion => 1;

    /// <inheritdoc />
    protected override int Precedence => 20;
}
