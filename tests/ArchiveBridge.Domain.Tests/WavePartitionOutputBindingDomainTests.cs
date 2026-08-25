using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I5-010 — regras do vínculo IMUTÁVEL entre uma onda e um output de particionamento canônico: só pode
/// ser criado a partir de uma <see cref="PartitionExecutionRecord"/> já verificada (nunca de IDs soltos),
/// nunca cruza escopo de tenant/projeto, e a fronteira Create/Rehydrate NÃO CONFIÁVEL já usada em
/// <c>PurviewSasUploadHandle</c>/<c>PartitionExecutionRecord</c> se aplica igualmente aqui.
/// AB-I5-013 — a correlação com a <see cref="WaveEntryId"/> de destino é parte do CONTEÚDO protegido pelo
/// hash e da comparação de convergência idempotente/incompatibilidade (<see cref="WavePartitionOutputBinding.IsSameLogicalOutputAs"/>).
/// </summary>
public sealed class WavePartitionOutputBindingDomainTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly PartitionExecutorIdentity Executor = new("TestExecutor", "1.0");

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static WaveEntryId NewEntryId(WaveId wave, string seed = "mailbox@contoso.com") =>
        WaveEntryId.Derive(wave, new WaveEntry($"C:\\pst\\{seed}.pst", $"{seed}.pst", new ArchiveRef(seed), 4096, 10));

    private static PartitionExecutionRecord NewExecution(TenantId tenant, ProjectId project, Sha256Hash? planHash = null, int sequence = 1)
    {
        var hash = planHash ?? Hash("plan");
        var sourceHash = Hash("source-bytes");
        return PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), tenant, project, ArtifactId.New(), PartitionPlanId.New(), PartitionPlanPartId.New(),
            hash, sequence, PartitionPlanIdentity.ComputePartKey(hash, sequence), sourceHash, 4096, sourceHash, 4096,
            Executor, CorrelationId.New(), StartedAt, CompletedAt);
    }

    [Fact]
    public void CreateReidratesPlanPartExecutionArtifactPartKeyAndOutputFromTheExecutionRecordNeverFromLooseArguments()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var entry = NewEntryId(wave);
        var execution = NewExecution(tenant, project);

        var binding = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, entry, execution, CorrelationId.New(), Now);

        Assert.Equal(execution.Plan, binding.Plan);
        Assert.Equal(execution.Part, binding.Part);
        Assert.Equal(execution.Id, binding.Execution);
        Assert.Equal(execution.Artifact, binding.Artifact);
        Assert.Equal(execution.PartKey, binding.PartKey);
        Assert.Equal(execution.OutputHash, binding.OutputHash);
        Assert.Equal(execution.OutputSizeBytes, binding.OutputSizeBytes);
        Assert.Equal(wave, binding.Wave);
        Assert.Equal(entry, binding.Entry);
    }

    [Fact]
    public void CreateRejectsAnExecutionFromADifferentTenantThanTheBindingScope()
    {
        var bindingTenant = new TenantId(Guid.NewGuid());
        var executionTenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var execution = NewExecution(executionTenant, project);

        Assert.Throws<ArgumentException>(() =>
            WavePartitionOutputBinding.Create(
                WavePartitionOutputBindingId.New(), bindingTenant, project, wave, NewEntryId(wave), execution,
                CorrelationId.New(), Now));
    }

    [Fact]
    public void CreateRejectsAnExecutionFromADifferentProjectThanTheBindingScope()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var bindingProject = new ProjectId(Guid.NewGuid());
        var executionProject = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var execution = NewExecution(tenant, executionProject);

        Assert.Throws<ArgumentException>(() =>
            WavePartitionOutputBinding.Create(
                WavePartitionOutputBindingId.New(), tenant, bindingProject, wave, NewEntryId(wave), execution,
                CorrelationId.New(), Now));
    }

    [Fact]
    public void RehydrateFailsClosedWhenBindingHashDoesNotMatchLoadedFields()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var execution = NewExecution(tenant, project);
        var binding = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, NewEntryId(wave), execution, CorrelationId.New(), Now);

        Assert.Throws<WavePartitionOutputBindingIntegrityViolationException>(() =>
            WavePartitionOutputBinding.Rehydrate(
                binding.Id, binding.Tenant, binding.Project, binding.Wave, binding.Entry, binding.Plan, binding.Part,
                binding.Execution, binding.Artifact, binding.PartKey, binding.OutputHash, binding.OutputSizeBytes,
                binding.Correlation, binding.CreatedAtUtc, Hash("tampered-hash")));
    }

    [Fact]
    public void RehydrateFailsClosedWhenTheEntryCorrelationWasTamperedEvenIfEveryOtherFieldMatches()
    {
        // AB-I5-013 item 5: entry_id faz parte do binding_hash — trocar SOMENTE a entrada persistida
        // (mantendo todos os demais campos) deve ser detectado como adulteração, não silenciosamente aceito.
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var execution = NewExecution(tenant, project);
        var binding = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, NewEntryId(wave, "original"), execution, CorrelationId.New(), Now);
        var swappedEntry = NewEntryId(wave, "swapped");

        Assert.Throws<WavePartitionOutputBindingIntegrityViolationException>(() =>
            WavePartitionOutputBinding.Rehydrate(
                binding.Id, binding.Tenant, binding.Project, binding.Wave, swappedEntry, binding.Plan, binding.Part,
                binding.Execution, binding.Artifact, binding.PartKey, binding.OutputHash, binding.OutputSizeBytes,
                binding.Correlation, binding.CreatedAtUtc, binding.BindingHash));
    }

    [Fact]
    public void RehydrateSucceedsWhenTheHashMatchesTheLoadedFields()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var execution = NewExecution(tenant, project);
        var binding = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, NewEntryId(wave), execution, CorrelationId.New(), Now);

        var rehydrated = WavePartitionOutputBinding.Rehydrate(
            binding.Id, binding.Tenant, binding.Project, binding.Wave, binding.Entry, binding.Plan, binding.Part,
            binding.Execution, binding.Artifact, binding.PartKey, binding.OutputHash, binding.OutputSizeBytes,
            binding.Correlation, binding.CreatedAtUtc, binding.BindingHash);

        Assert.Equal(binding, rehydrated);
    }

    [Fact]
    public void IsSameLogicalOutputAsIsTrueForTwoBindingsOfTheSameWaveEntryAndExecutionRegardlessOfIdOrCorrelation()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var entry = NewEntryId(wave);
        var execution = NewExecution(tenant, project);

        var first = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, entry, execution, CorrelationId.New(), Now);
        var second = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, entry, execution, CorrelationId.New(), Now.AddMinutes(1));

        Assert.True(first.IsSameLogicalOutputAs(second));
    }

    [Fact]
    public void IsSameLogicalOutputAsIsFalseWhenTheExecutionDiffers()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var entry = NewEntryId(wave);
        var planHash = Hash("shared-plan-hash");
        var firstExecution = NewExecution(tenant, project, planHash);
        var secondExecution = NewExecution(tenant, project, planHash);

        var first = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, entry, firstExecution, CorrelationId.New(), Now);
        var second = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, entry, secondExecution, CorrelationId.New(), Now);

        Assert.False(first.IsSameLogicalOutputAs(second));
    }

    [Fact]
    public void IsSameLogicalOutputAsIsFalseWhenTheWaveDiffersEvenForTheSameExecution()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var firstWave = WaveId.New();
        var secondWave = WaveId.New();
        var execution = NewExecution(tenant, project);

        var first = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, firstWave, NewEntryId(firstWave), execution, CorrelationId.New(), Now);
        var second = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, secondWave, NewEntryId(secondWave), execution, CorrelationId.New(), Now);

        Assert.False(first.IsSameLogicalOutputAs(second));
    }

    [Fact]
    public void IsSameLogicalOutputAsIsFalseWhenOnlyTheEntryDiffersForTheSameWaveAndExecution()
    {
        // AB-I5-013 item 4: mesmo output físico, mesma onda, mas apontando para uma entrada de destino
        // diferente — reassignação ambígua, nunca convergência idempotente.
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var execution = NewExecution(tenant, project);

        var first = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, NewEntryId(wave, "mailbox-a"), execution, CorrelationId.New(), Now);
        var second = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, NewEntryId(wave, "mailbox-b"), execution, CorrelationId.New(), Now);

        Assert.False(first.IsSameLogicalOutputAs(second));
    }
}
