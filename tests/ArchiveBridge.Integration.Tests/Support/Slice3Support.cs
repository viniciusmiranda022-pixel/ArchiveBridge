using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Time;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>Fábricas e fixtures compartilhados pelos testes de integração da Vertical Slice 3 (EV discovery).</summary>
internal static class Slice3Support
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // Snapshot "pronto": PowerShell/módulo/directory/sites/servers/vaultstores/archives/staging/permissões OK.
    public const string ReadySnapshotJson =
        "{\"powershellAvailable\":true,\"moduleAvailable\":true,\"snapinAvailable\":false,\"directoryConnectivity\":true," +
        "\"sites\":1,\"servers\":2,\"vaultStores\":1,\"archives\":5,\"stagingPathAccess\":true,\"requiredPermissions\":true," +
        "\"permissionDenied\":false,\"observedVersion\":\"14.2\",\"productVersion\":\"14.2\",\"discoverySource\":\"PowerShell/Module\"}";

    // Assinatura MODERNA (OutputPath + tamanho/relatório/filtragem) — reconhecida pelo adapter moderno.
    public const string ModernCommandJson =
        "{\"available\":true,\"signature\":{\"commandName\":\"Export-EVArchive\",\"moduleSource\":\"Mod\",\"version\":\"14.2\"," +
        "\"commandType\":\"Cmdlet\",\"parameters\":[\"ArchiveId\",\"OutputPath\",\"MaxSizeMB\",\"GenerateReport\",\"Filter\"]," +
        "\"mandatory\":[\"ArchiveId\",\"OutputPath\"],\"parameterSets\":[\"Default\"]}}";

    // Snapshot "bloqueado": permissões ausentes.
    public const string BlockedSnapshotJson =
        "{\"powershellAvailable\":true,\"moduleAvailable\":true,\"snapinAvailable\":false,\"directoryConnectivity\":true," +
        "\"sites\":1,\"servers\":2,\"vaultStores\":1,\"archives\":5,\"stagingPathAccess\":true,\"requiredPermissions\":false," +
        "\"permissionDenied\":false,\"observedVersion\":\"14.2\",\"productVersion\":\"14.2\",\"discoverySource\":\"PowerShell/Module\"}";

    public static FixtureEvPowerShellHost ReadyHost() => new(ReadySnapshotJson, ModernCommandJson);

    public static GuardedEvPowerShellExecutor Executor(IEvPowerShellHost host) =>
        new(host, ["Get-Command", PowerShellEvCapabilityDiscovery.SnapshotCommand], 1L * 1024 * 1024);

    public static PowerShellEvCapabilityDiscovery Discovery(IEvPowerShellHost host, IClock clock) => new(Executor(host), clock);

    public static SqlEvDiscoveryStore Store(SqlServerFixture fixture, IClock clock) => new(fixture.Factory, clock);

    public static FileSystemEvDiscoveryEvidenceStore Evidence(SqlServerFixture fixture) => new(fixture.ArtifactRoot);

    public static SqlEvDiscoveryCommandInbox Inbox(SqlServerFixture fixture, IClock clock) => new(fixture.Factory, clock);

    public static AdapterCompatibilityEvaluator Adapters() => new([new EvExportModernAdapter(), new EvExportLegacyAdapter()]);

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
            clock);

    public static EvEnvironmentDescriptor NewEnvironment() =>
        new(new EvEnvironmentId(Guid.NewGuid()), "site-a", "dir-a.contoso.local");

    /// <summary>Contexto do comando alinhado ao projeto recém-criado (versão/hash de configuração).</summary>
    public static EvDiscoveryCommandContext Context(Sha256Hash configurationHash) =>
        new(EvDiscoveryCommandContext.CurrentSchemaVersion, 1, configurationHash, EvDiscoveryPolicy.CurrentVersion);
}

/// <summary>Host PowerShell de FIXTURE: devolve saídas JSON controladas por comando (sem Enterprise Vault real).</summary>
internal sealed class FixtureEvPowerShellHost(string snapshotJson, string commandJson) : IEvPowerShellHost
{
    public string SnapshotJson { get; set; } = snapshotJson;

    public string CommandJson { get; set; } = commandJson;

    public Task<EvPowerShellHostOutput> RunAsync(EvPowerShellRequest request, CancellationToken cancellationToken)
    {
        var json = string.Equals(request.CommandName, PowerShellEvCapabilityDiscovery.SnapshotCommand, StringComparison.Ordinal)
            ? SnapshotJson
            : CommandJson;
        return Task.FromResult(new EvPowerShellHostOutput(0, json, string.Empty, false));
    }
}
