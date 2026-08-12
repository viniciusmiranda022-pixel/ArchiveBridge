using ArchiveBridge.Workers.Ev;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Configuração fail-closed do Worker EV e fronteira de plataforma. Prova que, habilitado, connection
/// strings/raiz de evidência ausentes ou limites inválidos derrubam o startup (nunca degradam), e que um
/// host não-Windows recusa executar o Enterprise Vault real (nunca "Ready" com sonda falsa em produção).
/// </summary>
public sealed class EvWorkerConfigurationTests
{
    private static EnterpriseVaultDiscoveryOptions ValidOptions() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 5,
        LeaseSeconds = 30,
        MaxScopesPerPoll = 32,
        EvidenceRoot = "/var/opt/archivebridge/evidence",
    };

    [Fact]
    public void ValidOperationalConfigurationPasses()
    {
        var exception = Record.Exception(() =>
            ValidOptions().ValidateForOperation("Server=app;Database=ab", "Server=maint;Database=ab"));
        Assert.Null(exception);
    }

    [Fact]
    public void MissingApplicationConnectionFailsClosed() =>
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => ValidOptions().ValidateForOperation("", "Server=maint"));

    [Fact]
    public void MissingMaintenanceConnectionFailsClosed() =>
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => ValidOptions().ValidateForOperation("Server=app", "   "));

    [Fact]
    public void MissingEvidenceRootFailsClosed()
    {
        var options = ValidOptions();
        options.EvidenceRoot = "";
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => options.ValidateForOperation("Server=app", "Server=maint"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositivePollIntervalFailsClosed(int seconds)
    {
        var options = ValidOptions();
        options.PollIntervalSeconds = seconds;
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => options.ValidateForOperation("Server=app", "Server=maint"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLeaseFailsClosed(int seconds)
    {
        var options = ValidOptions();
        options.LeaseSeconds = seconds;
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => options.ValidateForOperation("Server=app", "Server=maint"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(EnterpriseVaultDiscoveryOptions.MaxScopesPerPollUpperBound + 1)]
    public void OutOfRangeMaxScopesFailsClosed(int max)
    {
        var options = ValidOptions();
        options.MaxScopesPerPoll = max;
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => options.ValidateForOperation("Server=app", "Server=maint"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLeaseRecoveryIntervalFailsClosed(int seconds)
    {
        var options = ValidOptions();
        options.LeaseRecoveryIntervalSeconds = seconds;
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => options.ValidateForOperation("Server=app", "Server=maint"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(EnterpriseVaultDiscoveryOptions.LeaseRecoveryBatchSizeUpperBound + 1)]
    public void OutOfRangeLeaseRecoveryBatchSizeFailsClosed(int batch)
    {
        var options = ValidOptions();
        options.LeaseRecoveryBatchSize = batch;
        Assert.Throws<EnterpriseVaultDiscoveryConfigurationException>(
            () => options.ValidateForOperation("Server=app", "Server=maint"));
    }

    // §17 — DI fail-closed: sem habilitação, NENHUM worker EV (nem discovery, nem reaper) é registrado.
    [Fact]
    public void DisabledCompositionRegistersNoEvWorkers()
    {
        var builder = Host.CreateApplicationBuilder();
        EnterpriseVaultDiscoveryComposition.Configure(builder);

        Assert.DoesNotContain(builder.Services, d => d.ImplementationType == typeof(EvDiscoveryWorker));
        Assert.DoesNotContain(builder.Services, d => d.ImplementationType == typeof(EvLeaseRecoveryWorker));
    }

    // §17 — quando habilitado, a composição registra AMBOS os workers (o registro ocorre no ramo habilitado,
    // após a validação e a checagem de plataforma). Prova estrutural: em host não-Windows o ramo habilitado
    // é fail-closed em runtime, então validamos o registro pela fonte da composição.
    [Fact]
    public void EnabledCompositionWiresBothDiscoveryAndReaperWorkers()
    {
        var composition = File.ReadAllText(Path.Combine(
            ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Workers.Ev", "EnterpriseVaultDiscoveryComposition.cs"));

        Assert.Contains("AddHostedService<EvDiscoveryWorker>", composition, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<EvLeaseRecoveryWorker>", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void NonWindowsHostFailsClosedForRealEv() =>
        Assert.Throws<PlatformNotSupportedException>(
            () => EnterpriseVaultDiscoveryComposition.EnsureRealEvHostSupported(isWindowsHost: false));

    [Fact]
    public void WindowsHostIsAcceptedForRealEv()
    {
        var exception = Record.Exception(
            () => EnterpriseVaultDiscoveryComposition.EnsureRealEvHostSupported(isWindowsHost: true));
        Assert.Null(exception);
    }
}
