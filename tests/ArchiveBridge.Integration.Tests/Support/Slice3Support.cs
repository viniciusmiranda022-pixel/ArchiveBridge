using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>Fábricas e fixtures compartilhados pelos testes de integração da Vertical Slice 3 (EV discovery).</summary>
internal static class Slice3Support
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Host de fixture "pronto": todas as sondas retornam sucesso com um ambiente EV 15.1 completo.</summary>
    public static FixtureEvPowerShellHost ReadyHost() => new();

    /// <summary>Host de fixture com PERMISSÃO NEGADA apenas na sonda de archives (as demais continuam OK).</summary>
    public static FixtureEvPowerShellHost ArchivePermissionDeniedHost()
    {
        var host = new FixtureEvPowerShellHost();
        host.Set(EvPowerShellProbeKind.EvArchive, FixtureEvPowerShellHost.FailureEnvelope("EvArchive", "PERMISSION_DENIED"));
        return host;
    }

    public static PowerShellEvCapabilityDiscovery Discovery(IEvPowerShellHost host, IClock clock) => new(host, clock);

    public static SqlEvDiscoveryStore Store(SqlServerFixture fixture, IClock clock) => new(fixture.Factory, clock);

    public static FileSystemEvDiscoveryEvidenceStore Evidence(SqlServerFixture fixture) => new(fixture.ArtifactRoot);

    public static SqlEvDiscoveryCommandInbox Inbox(SqlServerFixture fixture, IClock clock) => new(fixture.Factory, clock);

    public static AdapterCompatibilityEvaluator Adapters() => new([new EvExportDocumented151Adapter()]);

    public static DiscoverEvCapabilitiesUseCase UseCase(SqlServerFixture fixture, IEvPowerShellHost host, IClock clock) =>
        new(Discovery(host, clock), Adapters(), new EvDiscoveryEvidenceSerializer(), Store(fixture, clock), Evidence(fixture), clock);

    public static EvDiscoveryCommandProcessor Processor(SqlServerFixture fixture, IEvPowerShellHost host, IClock clock) =>
        new(
            Inbox(fixture, clock),
            fixture.Store(clock),
            fixture.LeaseManager(clock, RetryPolicy.Default, Lease),
            Slice2Support.ProjectStore(fixture, clock),
            UseCase(fixture, host, clock),
            EvDiscoveryPolicy.Default,
            clock,
            RetryPolicy.Default);

    public static EvEnvironmentDescriptor NewEnvironment() =>
        new(new EvEnvironmentId(Guid.NewGuid()), "site-a", "dir-a.contoso.local");

    /// <summary>
    /// Simula a fase de reserva (probe → seleção → avaliação → hashes → staging → ReserveAsync), opcionalmente
    /// publicando a evidência, para os testes de reconciliação/concorrência. Devolve a reserva.
    /// </summary>
    public static async Task<EvDiscoveryReservation> ReserveAsync(
        SqlServerFixture fixture, TenantScope scope, EvEnvironmentDescriptor environment, IEvPowerShellHost host, IClock clock,
        int projectConfigVersion, Sha256Hash projectConfigHash, bool publish, JobFence? fence = null)
    {
        var policy = EvDiscoveryPolicy.Default;
        var observation = await Discovery(host, clock).ProbeAsync(environment, policy, CancellationToken.None);
        var selection = Adapters().Select(observation, policy);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, selection, policy, clock.UtcNow, clock.UtcNow);
        var configuration = EvDiscoveryConfiguration.For(environment.EnvironmentId, policy, projectConfigVersion, projectConfigHash);
        var configurationHash = configuration.ComputeHash();
        var semanticHash = EvDiscoverySemanticFingerprint.Compute(result);
        var bytes = new EvDiscoveryEvidenceSerializer().Serialize(result, configurationHash, semanticHash);
        var evidence = Evidence(fixture);
        var staging = await evidence.StageAsync(bytes.Bytes, bytes.ContentSha256, CancellationToken.None);
        var reservation = await Store(fixture, clock).ReserveAsync(
            scope, environment.EnvironmentId, result, configurationHash, semanticHash,
            bytes.ContentSha256, staging.SizeBytes, CorrelationId.New(), fence, CancellationToken.None);
        if (publish)
        {
            await evidence.PublishAsync(staging, new EvDiscoveryEvidenceDescriptor(scope, environment.EnvironmentId, reservation.DiscoveryVersion), CancellationToken.None);
        }
        else
        {
            await evidence.DiscardAsync(staging, CancellationToken.None);
        }

        return reservation;
    }
}

/// <summary>
/// Host PowerShell de FIXTURE: mapeia cada <see cref="EvPowerShellProbeKind"/> a um envelope JSON
/// versionado controlado (sem Enterprise Vault real). Por padrão devolve um ambiente EV 15.1 "pronto";
/// probes individuais podem ser substituídos por sucesso/falha específicos.
/// </summary>
internal sealed class FixtureEvPowerShellHost : IEvPowerShellHost
{
    private readonly Dictionary<EvPowerShellProbeKind, string> _envelopes;
    private readonly Dictionary<EvPowerShellProbeKind, EvProbeExecution> _executions = new();

    public FixtureEvPowerShellHost() => _envelopes = new Dictionary<EvPowerShellProbeKind, string>(DefaultReady());

    /// <summary>Substitui o envelope de uma sonda específica.</summary>
    public void Set(EvPowerShellProbeKind kind, string envelopeJson) => _envelopes[kind] = envelopeJson;

    /// <summary>Substitui a execução BRUTA de uma sonda (para simular falha mecânica: exit≠0, stderr, timeout, limite).</summary>
    public void SetExecution(EvPowerShellProbeKind kind, EvProbeExecution execution) => _executions[kind] = execution;

    public Task<EvProbeExecution> RunAsync(EvProbeRequest request, CancellationToken cancellationToken)
    {
        if (_executions.TryGetValue(request.Kind, out var execution))
        {
            return Task.FromResult(execution);
        }

        var json = _envelopes[request.Kind];
        return Task.FromResult(new EvProbeExecution(0, json, string.Empty, false, false));
    }

    /// <summary>Envelope de sucesso com o payload de dados indicado.</summary>
    public static string SuccessEnvelope(string probe, string dataJson) =>
        $"{{\"schemaVersion\":1,\"probe\":\"{probe}\",\"success\":true,\"errorCode\":null,\"data\":{dataJson}}}";

    /// <summary>Envelope de falha com o código de erro indicado.</summary>
    public static string FailureEnvelope(string probe, string errorCode) =>
        $"{{\"schemaVersion\":1,\"probe\":\"{probe}\",\"success\":false,\"errorCode\":\"{errorCode}\",\"data\":{{}}}}";

    private static Dictionary<EvPowerShellProbeKind, string> DefaultReady() => new()
    {
        [EvPowerShellProbeKind.PowerShellEnvironment] = SuccessEnvelope("PowerShellEnvironment", "{\"psVersion\":\"7.4.0\"}"),
        [EvPowerShellProbeKind.AvailableEvModule] = SuccessEnvelope("AvailableEvModule", "{\"present\":true}"),
        [EvPowerShellProbeKind.RegisteredEvSnapin] = SuccessEnvelope("RegisteredEvSnapin", "{\"present\":false}"),
        [EvPowerShellProbeKind.EvSite] = SuccessEnvelope("EvSite", "{\"count\":1}"),
        [EvPowerShellProbeKind.EvServer] = SuccessEnvelope("EvServer", "{\"count\":2}"),
        [EvPowerShellProbeKind.EvVaultStore] = SuccessEnvelope("EvVaultStore", "{\"count\":1}"),
        [EvPowerShellProbeKind.EvArchive] = SuccessEnvelope("EvArchive", "{\"count\":5}"),
        [EvPowerShellProbeKind.StagingPathAccess] = SuccessEnvelope("StagingPathAccess", "{\"accessible\":true}"),
        [EvPowerShellProbeKind.ExportEvArchiveCommandMetadata] = SuccessEnvelope(
            "ExportEvArchiveCommandMetadata",
            "{\"available\":true,\"observedVersion\":\"15.1\",\"productVersion\":\"15.1\",\"signature\":{\"commandName\":\"Export-EVArchive\"," +
            "\"moduleSource\":\"Symantec.EnterpriseVault.PowerShell\",\"version\":\"15.1\",\"commandType\":\"Cmdlet\"," +
            "\"parameters\":[\"ArchiveId\",\"OutputDirectory\",\"SearchString\",\"Format\",\"MaxThreads\",\"Retry\",\"MaxPSTSizeMB\"]," +
            "\"mandatory\":[\"ArchiveId\",\"OutputDirectory\"],\"parameterSets\":[\"Default\"]}}"),
    };
}
