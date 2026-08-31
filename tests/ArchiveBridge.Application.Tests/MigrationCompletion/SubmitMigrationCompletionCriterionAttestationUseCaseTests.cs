using ArchiveBridge.Application.MigrationCompletion;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;
using Canary = ArchiveBridge.Application.Tests.Canary;

namespace ArchiveBridge.Application.Tests.MigrationCompletion;

/// <summary>
/// AB-I8-010/AB-I8-011 — <see cref="SubmitMigrationCompletionCriterionAttestationUseCase"/>: RBAC server-side,
/// bloqueio estrutural contra atestar um critério SystemDerived OU EvidenceDerived (AB-I8-011: disposition de
/// fontes/parts, publicação WORM, ausência de credencial temporária — técnicos/objetivos, sem store canônico
/// suficiente), e Pass exige evidência real (nunca aprovação implícita — escopo obrigatório item 8, aplicável
/// explicitamente a "cliente aprovou relatório final" e a "janela de rollback/decommission definida").
/// </summary>
public sealed class SubmitMigrationCompletionCriterionAttestationUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static SubmitMigrationCompletionCriterionAttestationUseCase BuildUseCase(
        InMemoryMigrationCompletionCriterionAttestationStore store, Contracts.Abstractions.IAuthenticatedActorAccessor actorAccessor) =>
        new(store, new Canary.FixedClock(Now), actorAccessor);

    [Fact]
    public async Task AnonymousActorIsRejectedBeforeAnyScopedAccess()
    {
        var store = new InMemoryMigrationCompletionCriterionAttestationStore();
        var useCase = BuildUseCase(store, new Canary.UnauthenticatedActorAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                NewScope(), new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), ReadinessControlStatus.Pass,
                "customer-signoff:final-report-v1", ReasonCode: string.Empty, Correlation: CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task AViewerRoleCannotSubmitAnAttestation()
    {
        var store = new InMemoryMigrationCompletionCriterionAttestationStore();
        var useCase = BuildUseCase(store, new Canary.FakeAuthenticatedActorAccessor("alice", "Viewer"));

        await Assert.ThrowsAsync<MigrationCompletionAuthorizationException>(() => useCase.ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                NewScope(), new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), ReadinessControlStatus.Pass,
                "customer-signoff:final-report-v1", ReasonCode: string.Empty, Correlation: CorrelationId.New()),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("COMPLETION.RECONCILIATION_CLOSED")]
    [InlineData("COMPLETION.PROVIDER_RESULTS_COLLECTED")]
    public async Task AttestingASystemDerivedCriterionIsRefused(string systemDerivedCriterionId)
    {
        var store = new InMemoryMigrationCompletionCriterionAttestationStore();
        var useCase = BuildUseCase(store, new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));

        await Assert.ThrowsAsync<MigrationCompletionAttestationNotAllowedException>(() => useCase.ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                NewScope(), new MigrationCompletionCriterionId(systemDerivedCriterionId), ReadinessControlStatus.Pass,
                "manual override attempt", ReasonCode: string.Empty, Correlation: CorrelationId.New()),
            CancellationToken.None));

        Assert.Empty(await store.GetLatestForAllAsync(NewScope(), CancellationToken.None));
    }

    // AB-I8-011: os quatro critérios EvidenceDerived são tecnicamente objetivos e este repositório ainda não
    // possui um store canônico suficiente para nenhum deles — uma atestação humana, mesmo de um ator
    // autorizado, NUNCA pode substituir essa ausência (mesmo bloqueio estrutural de um critério SystemDerived).
    [Theory]
    [InlineData("COMPLETION.SOURCE_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.PARTS_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM")]
    [InlineData("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL")]
    public async Task AttestingAnEvidenceDerivedCriterionIsRefused(string evidenceDerivedCriterionId)
    {
        var store = new InMemoryMigrationCompletionCriterionAttestationStore();
        var useCase = BuildUseCase(store, new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));

        await Assert.ThrowsAsync<MigrationCompletionAttestationNotAllowedException>(() => useCase.ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                NewScope(), new MigrationCompletionCriterionId(evidenceDerivedCriterionId), ReadinessControlStatus.Pass,
                "manual override attempt", ReasonCode: string.Empty, Correlation: CorrelationId.New()),
            CancellationToken.None));

        Assert.Empty(await store.GetLatestForAllAsync(NewScope(), CancellationToken.None));
    }

    [Fact]
    public async Task AnApproverCanAttestCustomerFinalApprovalWithRealEvidence()
    {
        var store = new InMemoryMigrationCompletionCriterionAttestationStore();
        var scope = NewScope();
        var useCase = BuildUseCase(store, new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));

        var attestation = await useCase.ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                scope, new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), ReadinessControlStatus.Pass,
                "customer-signoff:final-report-v1", ReasonCode: string.Empty, Correlation: CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, attestation.Status);
        Assert.Equal("alice", attestation.SubmittedBy);
        Assert.Equal("Approver", attestation.SubmittedByRole);
        Assert.NotEqual(ReadinessEvidenceKind.None, attestation.Evidence.Kind);
    }

    [Fact]
    public async Task IdenticalReplayConvergesToTheSameAttestationVersion()
    {
        var store = new InMemoryMigrationCompletionCriterionAttestationStore();
        var scope = NewScope();
        var useCase = BuildUseCase(store, new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var command = new SubmitMigrationCompletionCriterionAttestationCommand(
            scope, new MigrationCompletionCriterionId("COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED"), ReadinessControlStatus.Pass,
            "rollback-window-definition:v1", ReasonCode: string.Empty, Correlation: CorrelationId.New());

        var first = await useCase.ExecuteAsync(command, CancellationToken.None);
        var second = await useCase.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(first.AttestationVersion, second.AttestationVersion);
    }
}
