using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Workers.Pst;
using ArchiveBridge.Workers.Pst.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Slice 4B, Passo 3 — configuração e composição FAIL-CLOSED da execução de particionamento: sem habilitação
/// explícita nada é registrado; configuração incoerente (executar sem planejamento, raiz de output
/// ausente/igual à raiz de origem) derruba o startup em vez de degradar silenciosamente.
/// </summary>
public sealed class PstPartitionExecutionConfigurationTests
{
    private static PstPartitionExecutionOptions Enabled(string outputRoot = "/var/ab/output") =>
        new() { Enabled = true, OutputRootPath = outputRoot };

    [Fact]
    public void DefaultsAreFailClosed()
    {
        var options = new PstPartitionExecutionOptions();
        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.OutputRootPath);
        Assert.True(options.MinFreeSpaceMarginBytes > 0);
        Assert.True(options.TimeoutSeconds > 0);
    }

    [Fact]
    public void ExecutionWithoutPlanningEnabledFailsClosed() =>
        Assert.Throws<PstInspectionConfigurationException>(
            () => Enabled().ValidateForOperation(planningEnabled: false, sourceRootPath: "/var/ab/source"));

    [Fact]
    public void MissingOutputRootFailsClosed()
    {
        var options = new PstPartitionExecutionOptions { Enabled = true, OutputRootPath = "" };
        Assert.Throws<PstInspectionConfigurationException>(
            () => options.ValidateForOperation(planningEnabled: true, sourceRootPath: "/var/ab/source"));
    }

    [Fact]
    public void OutputRootSameAsSourceRootFailsClosed()
    {
        var options = Enabled(outputRoot: "/var/ab/source");
        Assert.Throws<PstInspectionConfigurationException>(
            () => options.ValidateForOperation(planningEnabled: true, sourceRootPath: "/var/ab/source"));
    }

    [Fact]
    public void ValidConfigurationDoesNotThrow()
    {
        var options = Enabled();
        options.ValidateForOperation(planningEnabled: true, sourceRootPath: "/var/ab/source"); // não lança
    }

    [Theory]
    [InlineData(-1L)]
    public void NegativeSpaceMarginFailsClosed(long margin)
    {
        var options = Enabled();
        options.MinFreeSpaceMarginBytes = margin;
        Assert.Throws<PstInspectionConfigurationException>(
            () => options.ValidateForOperation(planningEnabled: true, sourceRootPath: "/var/ab/source"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeoutFailsClosed(int seconds)
    {
        var options = Enabled();
        options.TimeoutSeconds = seconds;
        Assert.Throws<PstInspectionConfigurationException>(
            () => options.ValidateForOperation(planningEnabled: true, sourceRootPath: "/var/ab/source"));
    }

    [Fact]
    public void DisabledCompositionRegistersNoExecutionService()
    {
        var builder = Host.CreateApplicationBuilder();
        PstInspectionComposition.Configure(builder);

        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(ExecutePartitionPlanUseCase));
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(IPartitionPartWriter));
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(IPartitionExecutionStore));
    }

    [Fact]
    public void ExecutionEnabledWithoutPlanningBringsTheHostDownInsteadOfBeingSilentlyIgnored()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{PstPartitionExecutionOptions.SectionName}:Enabled"] = "true",
            [$"{PstPartitionExecutionOptions.SectionName}:OutputRootPath"] = "/var/ab/output",
            [$"{PstPartitionPlanningOptions.SectionName}:Enabled"] = "false",
            [$"{PstInspectionOptions.SectionName}:Enabled"] = "true",
            [$"{PstInspectionOptions.SectionName}:RootPath"] = "/var/ab/source",
            ["ConnectionStrings:Application"] = "Server=localhost;Database=x;Integrated Security=true;",
            ["ConnectionStrings:Maintenance"] = "Server=localhost;Database=x;Integrated Security=true;",
        });

        Assert.Throws<PstInspectionConfigurationException>(() => PstInspectionComposition.Configure(builder));
    }

    [Fact]
    public void ExecutionEnabledWithSameRootAsSourceBringsTheHostDown()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{PstPartitionExecutionOptions.SectionName}:Enabled"] = "true",
            [$"{PstPartitionExecutionOptions.SectionName}:OutputRootPath"] = "/var/ab/source",
            [$"{PstPartitionPlanningOptions.SectionName}:Enabled"] = "true",
            [$"{PstInspectionOptions.SectionName}:Enabled"] = "true",
            [$"{PstInspectionOptions.SectionName}:RootPath"] = "/var/ab/source",
            ["ConnectionStrings:Application"] = "Server=localhost;Database=x;Integrated Security=true;",
            ["ConnectionStrings:Maintenance"] = "Server=localhost;Database=x;Integrated Security=true;",
        });

        Assert.Throws<PstInspectionConfigurationException>(() => PstInspectionComposition.Configure(builder));
    }

    [Fact]
    public void EnabledCompositionWiresExecutionWithADistinctOutputRoot()
    {
        var composition = File.ReadAllText(Path.Combine(
            ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Workers.Pst", "Composition", "PstInspectionComposition.cs"));

        Assert.Contains(
            "new ExecutePartitionPlanUseCase(custodyStore, planStore, executionStore, writer, clock)",
            composition, StringComparison.Ordinal);
        Assert.Contains("new SqlPartitionExecutionStore(connectionFactory)", composition, StringComparison.Ordinal);
        Assert.Contains("new LocalSinglePartExecutionWriter(storageOptions, outputOptions)", composition, StringComparison.Ordinal);
    }
}
