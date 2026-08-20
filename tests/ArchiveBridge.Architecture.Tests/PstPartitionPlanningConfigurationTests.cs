using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Workers.Pst;
using ArchiveBridge.Workers.Pst.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Slice 4B, Passo 2 — configuração e composição FAIL-CLOSED do planejamento de particionamento: sem
/// habilitação explícita nada é registrado; limites inválidos ou habilitação incoerente (planejar sem
/// inspeção) derrubam o startup em vez de degradar silenciosamente; e os defaults são exatamente os
/// documentados no runbook §20.1, nunca valores escolhidos pelo código.
/// </summary>
public sealed class PstPartitionPlanningConfigurationTests
{
    private static PstPartitionPlanningOptions Enabled() => new() { Enabled = true };

    [Fact]
    public void DefaultsComeFromTheDocumentedRunbookPolicy()
    {
        var options = new PstPartitionPlanningOptions();
        Assert.False(options.Enabled); // fail-closed por padrão
        Assert.Equal(PartitionPolicy.RunbookDefault.TargetPartBytes, options.TargetPartBytes);
        Assert.Equal(PartitionPolicy.RunbookDefault.HardPartBytes, options.HardPartBytes);
    }

    [Fact]
    public void ValidConfigurationMaterializesTheDocumentedPolicy()
    {
        var policy = Enabled().ValidateForOperation(inspectionEnabled: true);
        Assert.Equal(PartitionPolicy.RunbookDefault.Fingerprint, policy.Fingerprint);
    }

    [Fact]
    public void PlanningWithoutInspectionEnabledFailsClosed() =>
        Assert.Throws<PstInspectionConfigurationException>(
            () => Enabled().ValidateForOperation(inspectionEnabled: false));

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(-1L, 100L)]
    [InlineData(100L, 0L)]
    [InlineData(100L, -1L)]
    [InlineData(200L, 100L)]
    public void InvalidLimitsFailClosed(long target, long hard)
    {
        var options = Enabled();
        options.TargetPartBytes = target;
        options.HardPartBytes = hard;
        Assert.Throws<PstInspectionConfigurationException>(() => options.ValidateForOperation(inspectionEnabled: true));
    }

    [Fact]
    public void DisabledCompositionRegistersNoPlanningService()
    {
        var builder = Host.CreateApplicationBuilder();
        PstInspectionComposition.Configure(builder);

        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(PlanPstPartitionUseCase));
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(IPartitionPlanner));
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(IPartitionPlanStore));
    }

    [Fact]
    public void PlanningEnabledWithoutInspectionBringsTheHostDownInsteadOfBeingSilentlyIgnored()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{PstPartitionPlanningOptions.SectionName}:Enabled"] = "true",
            [$"{PstInspectionOptions.SectionName}:Enabled"] = "false",
        });

        Assert.Throws<PstInspectionConfigurationException>(() => PstInspectionComposition.Configure(builder));
    }

    [Fact]
    public void EnabledCompositionWiresPlanningWithoutGivingItThePstEngine()
    {
        var composition = File.ReadAllText(Path.Combine(
            ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Workers.Pst", "Composition", "PstInspectionComposition.cs"));

        Assert.Contains("new PlanPstPartitionUseCase(custodyStore, inspectionStore, planStore, planner, clock)",
            composition, StringComparison.Ordinal);
        Assert.Contains("new SqlPartitionPlanStore(connectionFactory)", composition, StringComparison.Ordinal);
        Assert.Contains("new SizeBoundedPartitionPlanner(partitionPolicy)", composition, StringComparison.Ordinal);
    }
}
