using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B7: a validação da onda grava avaliações + transição de estado ATOMICAMENTE (uma transação, com
/// concorrência otimista). Uma falha (row_version divergente) rola TUDO para trás — nenhuma avaliação
/// órfã, nenhuma transição sem evidência. Um retry limpo não duplica avaliações.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2ValidationAtomicityTests(SqlServerFixture fixture)
{
    private async Task<int> AssessmentCountAsync(TenantScope scope, Guid waveId)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.planning_assessments WHERE wave_id = @id;", connection.Connection);
        command.Parameters.AddWithValue("@id", waveId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task ConcurrencyConflictRollsBackAssessmentsThenRetrySucceedsOnce()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        var store = Slice2Support.WaveStore(fixture);
        await store.AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var stale = await store.GetAsync(scope, wave.Id, CancellationToken.None);
        var assessments = new List<PlanningAssessment>
        {
            new(stale!.Selection.Entries[0].Archive.Identity, 10, "WITHIN_LIMIT",
                CapacityAssessmentResult.WithinLimit, "dentro do limite", CorrelationId.New(), Slice2Support.Now, null),
        };

        // Um escritor concorrente avança a onda (row_version muda), tornando `stale` defasado.
        var concurrent = await store.GetAsync(scope, wave.Id, CancellationToken.None);
        concurrent!.StartValidation();
        await store.SaveStatusAsync(concurrent, CorrelationId.New(), CancellationToken.None);

        // A gravação atômica com o token defasado falha e rola TUDO para trás (nenhuma avaliação).
        stale.StartValidation();
        stale.MarkReadyForApproval();
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.SaveValidationAsync(stale, assessments, CorrelationId.New(), CancellationToken.None));
        Assert.Equal(0, await AssessmentCountAsync(scope, wave.Id.Value));

        // Retry limpo: recarrega o token corrente e grava — exatamente uma avaliação, sem duplicar.
        var fresh = await store.GetAsync(scope, wave.Id, CancellationToken.None);
        fresh!.MarkReadyForApproval();
        await store.SaveValidationAsync(fresh, assessments, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await AssessmentCountAsync(scope, wave.Id.Value));
    }
}
