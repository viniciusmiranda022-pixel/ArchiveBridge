using System.Data;
using System.Globalization;
using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4B, Passo 2 (SQL Server real) — Partition Planning: planejamento read-only a partir de inspeção
/// canônica, identidade determinística persistida, réplay idempotente, concorrência convergente, fingerprint
/// de política na identidade, estados explícitos de <c>Unsupported</c>/<c>Blocked</c> (nunca boundaries
/// inventados), anti-IDOR cross-tenant/projeto e ausência de PII/caminho na evidência.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4bPartitionPlanningTests(SqlServerFixture fixture)
{
    private static readonly PartitionPolicy TinyPolicy = PartitionPolicy.Create(100, 200);

    private async Task<TenantScope> NewProjectScopeAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture)
            .AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        return scope;
    }

    /// <summary>Registra custódia para o conteúdo informado (arquivo escrito sob a raiz de custódia).</summary>
    private async Task<(TenantScope Scope, ArtifactId Artifact)> RegisterAsync(byte[] bytes, string name)
    {
        var scope = await NewProjectScopeAsync();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(bytes), bytes.Length, CancellationToken.None);
        return (scope, artifact.Id);
    }

    /// <summary>Registra custódia E executa a inspeção canônica (pré-condição do planejamento).</summary>
    private async Task<(TenantScope Scope, ArtifactId Artifact, byte[] Bytes)> RegisterAndInspectAsync(
        byte[] bytes, string name)
    {
        var (scope, artifact) = await RegisterAsync(bytes, name);
        await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        return (scope, artifact, bytes);
    }

    private async Task<T> ScalarAsync<T>(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
    }

    private Task<int> PlanRowCountAsync(TenantScope scope, ArtifactId artifact) =>
        ScalarAsync<int>(
            scope,
            "SELECT COUNT(*) FROM dbo.pst_partition_plans WHERE artifact_id = @artifact AND project_id = @project;",
            ("@artifact", artifact.Value), ("@project", scope.Project.Value));

    // ---- Caminho feliz: planeja, persiste e NÃO toca o PST ----

    [Fact]
    public async Task ArtifactWithinTargetIsPlannedPersistedAndLeavesThePstByteForByteUntouched()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var (scope, artifact, _) = await RegisterAndInspectAsync(bytes, "planned.pst");

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Planned, plan.Outcome);
        Assert.Equal(PartitionPlanReason.SinglePartWithinTarget, plan.Reason);
        Assert.True(plan.IsCanonical);
        Assert.Equal(bytes.Length, plan.Source.SourceSizeBytes);
        Assert.Equal(PartitionPolicy.RunbookDefault.Fingerprint, plan.Policy.Fingerprint);

        var part = Assert.Single(plan.Parts);
        Assert.Equal(1, part.Sequence);
        Assert.Equal(bytes.Length, part.PlannedSizeBytes);
        Assert.True(part.CoversEntireSource);

        // O plano é INTENÇÃO: o PST de origem permanece byte-for-byte e nenhum PST de saída é criado.
        var root = Slice4bPstProcessingSupport.PstRoot(fixture);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "planned.pst")));
        Assert.Single(Directory.GetFiles(root, "planned*"));

        // Persistido e relegível pela identidade determinística.
        var reread = await Slice4bPstProcessingSupport.PlanStore(fixture)
            .FindCanonicalAsync(scope, artifact, plan.PlanHash, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal(plan.Id, reread!.Id);
        Assert.Equal(plan.PlannedAtUtc, reread.PlannedAtUtc); // OUTPUT inserted.* ⇒ mesma precisão persistida
        Assert.Equal(part.PartKey, Assert.Single(reread.Parts).PartKey);
    }

    [Fact]
    public async Task IdempotentReplayReturnsTheSameCanonicalPlanWithoutANewRow()
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "replay-plan.pst");
        var useCase = Slice4bPstProcessingSupport.PlanUseCase(fixture);

        var first = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.PlannedAtUtc, second.PlannedAtUtc);
        Assert.Equal(1, await PlanRowCountAsync(scope, artifact)); // nenhuma linha duplicada.
    }

    [Fact]
    public async Task ConcurrentPlanningOfTheSameArtifactConvergesToExactlyOneCanonicalPlan()
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "concurrent-plan.pst");

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Slice4bPstProcessingSupport.PlanUseCase(fixture)
                .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None)));

        Assert.All(results, plan => Assert.True(plan.IsCanonical));
        Assert.Single(results.Select(plan => plan.Id).Distinct());
        Assert.Equal(1, await PlanRowCountAsync(scope, artifact));
        Assert.Equal(1, await ScalarAsync<int>(
            scope,
            "SELECT COUNT(*) FROM dbo.pst_partition_plan_parts WHERE plan_id = @plan AND project_id = @project;",
            ("@plan", results[0].Id.Value), ("@project", scope.Project.Value)));
    }

    // ---- Identidade: policy/config fingerprint e hash de origem ----

    [Fact]
    public async Task ChangingThePolicyProducesANewIdentityAndNeverReusesThePreviousPlan()
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "policy-change.pst");

        var underRunbookPolicy = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        var otherPolicy = PartitionPolicy.Create(1_000_000, 2_000_000);
        var underOtherPolicy = await Slice4bPstProcessingSupport.PlanUseCase(fixture, otherPolicy)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.NotEqual(underRunbookPolicy.PlanHash, underOtherPolicy.PlanHash);
        Assert.NotEqual(underRunbookPolicy.Id, underOtherPolicy.Id);
        Assert.Equal(2, await PlanRowCountAsync(scope, artifact));

        // Cada identidade continua resolvendo para o SEU plano — o anterior nunca é servido para a nova
        // política, e a nova nunca sobrescreve a anterior (append-only).
        var store = Slice4bPstProcessingSupport.PlanStore(fixture);
        var reread = await store.FindCanonicalAsync(scope, artifact, underRunbookPolicy.PlanHash, CancellationToken.None);
        Assert.Equal(underRunbookPolicy.Id, reread!.Id);
        Assert.Equal(PartitionPolicy.RunbookDefault.Fingerprint, reread.Policy.Fingerprint);
        Assert.Equal(otherPolicy.Fingerprint, (await store.FindCanonicalAsync(
            scope, artifact, underOtherPolicy.PlanHash, CancellationToken.None))!.Policy.Fingerprint);
    }

    [Fact]
    public async Task TwoArtifactsWithIdenticalContentInDifferentProjectsNeverShareAPlanIdentity()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var (firstScope, firstArtifact, _) = await RegisterAndInspectAsync(bytes, "twin-a.pst");
        var (secondScope, secondArtifact, _) = await RegisterAndInspectAsync(bytes, "twin-b.pst");

        var first = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(firstScope, firstArtifact, CorrelationId.New(), CancellationToken.None);
        var second = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(secondScope, secondArtifact, CorrelationId.New(), CancellationToken.None);

        // Mesmo conteúdo e mesmo hash de origem, escopos distintos ⇒ identidades de plano distintas.
        Assert.Equal(first.Source.SourceHash, second.Source.SourceHash);
        Assert.NotEqual(first.PlanHash, second.PlanHash);
    }

    // ---- Fail-closed: sem inspeção canônica, origem alterada, diagnóstico inválido ----

    [Fact]
    public async Task PlanningAnArtifactThatWasNeverInspectedIsBlocked()
    {
        var (scope, artifact) = await RegisterAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "never-inspected.pst");

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Equal(PartitionPlanReason.CanonicalInspectionUnavailable, plan.Reason);
        Assert.False(plan.IsCanonical);
        Assert.Empty(plan.Parts);
        Assert.Null(plan.Source.Inspection);
        Assert.Equal(0, await ScalarAsync<int>(
            scope,
            "SELECT COUNT(*) FROM dbo.pst_partition_plans WHERE artifact_id = @artifact AND is_canonical = 1;",
            ("@artifact", artifact.Value)));
    }

    [Fact]
    public async Task AnArtifactChangedAfterRegistrationCannotBePlanned()
    {
        var original = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var scope = await NewProjectScopeAsync();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, "drifting-plan.pst", original);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(original), original.Length, CancellationToken.None);

        // A origem muda DEPOIS do registro: a inspeção é Stale, portanto não existe canônico algum.
        File.WriteAllBytes(
            Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "drifting-plan.pst"),
            Slice4bPstProcessingSupport.ValidUnicodeHeader(totalSize: 5000));
        var inspection = await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PstInspectionOutcome.Stale, inspection.Outcome);

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Equal(PartitionPlanReason.CanonicalInspectionUnavailable, plan.Reason);
        Assert.Empty(plan.Parts);
    }

    [Fact]
    public async Task AStructurallyInvalidPstIsBlockedEvenWithACanonicalInspection()
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.InvalidSignatureBytes(), "not-a-pst-plan.bin");

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Equal(PartitionPlanReason.StructuralDiagnosticNotValid, plan.Reason);
        Assert.NotNull(plan.Source.Inspection); // a inspeção EXISTE e é canônica; o PST é que não é planejável.
        Assert.Empty(plan.Parts);
    }

    // ---- Estado honesto: sem inventário de itens, não se inventa boundary ----

    [Fact]
    public async Task AnArtifactAboveTheTargetIsUnsupportedAndPersistsNoPartsAtAll()
    {
        var (scope, artifact, bytes) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "oversized-plan.pst");

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture, TinyPolicy)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Unsupported, plan.Outcome);
        Assert.Equal(PartitionPlanReason.ItemInventoryUnavailable, plan.Reason);
        Assert.False(plan.IsCanonical);
        Assert.Empty(plan.Parts);
        Assert.Equal(bytes.Length, plan.Source.SourceSizeBytes); // o observado é registrado honestamente...
        Assert.Equal(0, await ScalarAsync<int>(
            scope,
            "SELECT COUNT(*) FROM dbo.pst_partition_plan_parts WHERE plan_id = @plan;",
            ("@plan", plan.Id.Value))); // ...mas nenhuma parte/contagem/offset é fabricada.

        // Réplay: continua Unsupported, cada avaliação é sua própria evidência, nunca vira canônico.
        var again = await Slice4bPstProcessingSupport.PlanUseCase(fixture, TinyPolicy)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        Assert.NotEqual(plan.Id, again.Id);
        Assert.Equal(PartitionPlanOutcome.Unsupported, again.Outcome);
        Assert.Equal(0, await ScalarAsync<int>(
            scope,
            "SELECT COUNT(*) FROM dbo.pst_partition_plans WHERE artifact_id = @artifact AND is_canonical = 1;",
            ("@artifact", artifact.Value)));
    }

    // ---- Isolamento tenant/projeto (anti-IDOR) ----

    [Fact]
    public async Task CrossTenantAndCrossProjectPlanningIsDeniedIndistinguishablyFromNotFound()
    {
        var (ownerScope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "owned-plan.pst");
        var useCase = Slice4bPstProcessingSupport.PlanUseCase(fixture);
        var owned = await useCase.ExecuteAsync(ownerScope, artifact, CorrelationId.New(), CancellationToken.None);

        var crossProject = new TenantScope(ownerScope.Tenant, SqlServerFixture.NewScope().Project);
        var crossTenant = new TenantScope(SqlServerFixture.NewScope().Tenant, ownerScope.Project);

        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(crossProject, artifact, CorrelationId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(crossTenant, artifact, CorrelationId.New(), CancellationToken.None));

        // Mesmo conhecendo a identidade determinística exata, outro escopo não lê o plano (RLS + project_id).
        var store = Slice4bPstProcessingSupport.PlanStore(fixture);
        Assert.Null(await store.FindCanonicalAsync(crossProject, artifact, owned.PlanHash, CancellationToken.None));
        Assert.Null(await store.FindCanonicalAsync(crossTenant, artifact, owned.PlanHash, CancellationToken.None));
        Assert.NotNull(await store.FindCanonicalAsync(ownerScope, artifact, owned.PlanHash, CancellationToken.None));
    }

    // ---- Minimização de PII e imutabilidade dos checkpoints do Passo 1 ----

    [Fact]
    public async Task ThePersistedPlanCarriesNoPathFileNameOrMailboxIdentifier()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var scope = await NewProjectScopeAsync();
        const string relative = "mailboxes/alice.example.pst";
        Directory.CreateDirectory(Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "mailboxes"));
        Slice4bPstProcessingSupport.WriteFile(fixture, relative, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(bytes), bytes.Length, CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        foreach (var table in new[] { "dbo.pst_partition_plans", "dbo.pst_partition_plan_parts" })
        {
            foreach (var value in await ReadAllTextValuesAsync(scope, table, plan.Id))
            {
                Assert.DoesNotContain("alice", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("mailboxes", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".pst", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Slice4bPstProcessingSupport.PstRoot(fixture), value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task PlanningNeverWritesToTheCustodyOrInspectionCheckpointsOfStepOne()
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "readonly-plan.pst");

        var inspectionsBefore = await ScalarAsync<int>(
            scope, "SELECT COUNT(*) FROM dbo.pst_inspections WHERE artifact_id = @artifact;", ("@artifact", artifact.Value));
        var custodyBefore = await ScalarAsync<string>(
            scope, "SELECT registered_hash FROM dbo.pst_artifacts WHERE artifact_id = @artifact;", ("@artifact", artifact.Value));

        await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        await Slice4bPstProcessingSupport.PlanUseCase(fixture, TinyPolicy)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(inspectionsBefore, await ScalarAsync<int>(
            scope, "SELECT COUNT(*) FROM dbo.pst_inspections WHERE artifact_id = @artifact;", ("@artifact", artifact.Value)));
        Assert.Equal(custodyBefore, await ScalarAsync<string>(
            scope, "SELECT registered_hash FROM dbo.pst_artifacts WHERE artifact_id = @artifact;", ("@artifact", artifact.Value)));
    }

    // ---- Invariantes reforçados no BANCO, não apenas na aplicação ----

    [Theory]
    // Blocked/Unsupported com parte declarada (boundaries inventados) — CK_pst_partition_plans_outcome_fields.
    [InlineData(2, 2, 1, 0)]
    [InlineData(1, 1, 1, 0)]
    // Concluído sem is_canonical (canonicidade divergindo do Domain) — CK_pst_partition_plans_is_canonical.
    [InlineData(0, 0, 1, 0)]
    // Desfecho incoerente com o motivo — CK_pst_partition_plans_outcome_reason.
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 3, 1, 1)]
    public async Task TheDatabaseRejectsAPlanRowThatViolatesTheDomainInvariants(
        byte outcome, byte reason, int partCount, byte isCanonical)
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), $"db-invariant-{outcome}{reason}{partCount}{isCanonical}.pst");
        var inspection = await ScalarAsync<Guid>(
            scope,
            "SELECT TOP 1 inspection_id FROM dbo.pst_inspections WHERE artifact_id = @artifact AND is_canonical = 1;",
            ("@artifact", artifact.Value));

        var exception = await Assert.ThrowsAsync<SqlException>(() => InsertPlanRowAsync(
            scope, artifact, inspection, outcome, reason, partCount, isCanonical));

        Assert.Contains("CK_pst_partition_plans", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDatabaseRejectsAPlanBoundToAnInspectionOfAnotherArtifact()
    {
        var (scope, artifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "db-fk-owner.pst");
        var (otherScope, otherArtifact, _) = await RegisterAndInspectAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "db-fk-other.pst");
        var foreignInspection = await ScalarAsync<Guid>(
            otherScope,
            "SELECT TOP 1 inspection_id FROM dbo.pst_inspections WHERE artifact_id = @artifact AND is_canonical = 1;",
            ("@artifact", otherArtifact.Value));

        // A inspeção existe — mas pertence a outro artefato/escopo. O FK composto recusa a linha.
        var exception = await Assert.ThrowsAsync<SqlException>(() => InsertPlanRowAsync(
            scope, artifact, foreignInspection, outcome: 0, reason: 0, partCount: 1, isCanonical: 1));

        Assert.Contains("FK_pst_partition_plans_inspection", exception.Message, StringComparison.Ordinal);
    }

    private async Task InsertPlanRowAsync(
        TenantScope scope, ArtifactId artifact, Guid inspection, byte outcome, byte reason, int partCount, byte isCanonical)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.pst_partition_plans
                (plan_id, tenant_id, project_id, artifact_id, inspection_id, source_hash, source_size_bytes,
                 plan_hash, policy_fingerprint, target_part_bytes, hard_part_bytes, planner_name, planner_version,
                 engine_name, engine_version, outcome, reason, part_count, is_canonical, correlation_id, planned_at_utc)
            VALUES
                (NEWID(), @tenant, @project, @artifact, @inspection, REPLICATE('a', 64), 4096,
                 REPLICATE('b', 64), REPLICATE('c', 64), 100, 200, 'Probe', '1.0',
                 'Engine', '1.0', @outcome, @reason, @partCount, @isCanonical, NEWID(), SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = artifact.Value });
        command.Parameters.Add(new SqlParameter("@inspection", SqlDbType.UniqueIdentifier) { Value = inspection });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = outcome });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = reason });
        command.Parameters.Add(new SqlParameter("@partCount", SqlDbType.Int) { Value = partCount });
        command.Parameters.Add(new SqlParameter("@isCanonical", SqlDbType.Bit) { Value = isCanonical });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ReadAllTextValuesAsync(TenantScope scope, string table, PartitionPlanId plan)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        // O nome da tabela vem de uma lista fixa do próprio teste (nunca de entrada externa).
        await using var command = new SqlCommand($"SELECT * FROM {table} WHERE plan_id = @plan;", connection);
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = plan.Value });
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!reader.IsDBNull(index))
                {
                    values.Add(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }

        Assert.True(rows > 0, $"Nenhuma linha encontrada em {table} para o plano.");
        return values;
    }
}
