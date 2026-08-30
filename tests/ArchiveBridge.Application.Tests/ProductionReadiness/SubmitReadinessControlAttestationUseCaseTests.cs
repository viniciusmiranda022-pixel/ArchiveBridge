using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests.ProductionReadiness;

/// <summary>
/// AB-I8-001 — <see cref="SubmitReadinessControlAttestationUseCase"/>: RBAC server-side, e o bloqueio
/// estrutural que impede atestação manual de qualquer controle SystemDerived, mesmo por um ator autorizado a
/// escrever.
/// </summary>
public sealed class SubmitReadinessControlAttestationUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static SubmitReadinessControlAttestationUseCase BuildUseCase(
        Contracts.Abstractions.IAuthenticatedActorAccessor actorAccessor, InMemoryReadinessControlAttestationStore? store = null) =>
        new(store ?? new InMemoryReadinessControlAttestationStore(), new FixedClock(Now), actorAccessor);

    [Fact]
    public async Task AnonymousActorIsRejected()
    {
        var useCase = BuildUseCase(new UnauthenticatedActorAccessor());
        var command = new SubmitReadinessControlAttestationCommand(
            NewScope(), new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessControlStatus.Pass,
            "ADR-0031 approved 2026-08-15", ReasonCode: string.Empty, CorrelationId.New());

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task AViewerCannotSubmitAnAttestation()
    {
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Viewer"));
        var command = new SubmitReadinessControlAttestationCommand(
            NewScope(), new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessControlStatus.Pass,
            "ADR-0031 approved 2026-08-15", ReasonCode: string.Empty, CorrelationId.New());

        await Assert.ThrowsAsync<ProductionReadinessAuthorizationException>(() => useCase.ExecuteAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task AnApproverCanAttestAnAttestedControl()
    {
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Approver"));
        var command = new SubmitReadinessControlAttestationCommand(
            NewScope(), new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessControlStatus.Pass,
            "ADR-0031 approved 2026-08-15", ReasonCode: string.Empty, CorrelationId.New());

        var attestation = await useCase.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, attestation.Status);
        Assert.Equal("alice", attestation.SubmittedBy);
    }

    [Theory]
    [InlineData("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH")]
    [InlineData("OPS.RTO_EXERCISED")]
    [InlineData("OPS.RPO_EXERCISED")]
    [InlineData("SEC.SBOM_AND_SIGNATURES")]
    [InlineData("SEC.WDAC_DEFENDER_PATCHING")]
    [InlineData("SEC.INCIDENT_RESPONSE_EXERCISED")]
    [InlineData("DATA.HASHES_MANIFESTS_LINEAGE_WORM")]
    [InlineData("DATA.BACKUP_RESTORE_TESTED")]
    [InlineData("M365.TARGET_ROOT_POLICY")]
    [InlineData("M365.IMPORT_LIMITS_100GB_500ROWS")]
    public async Task EvenAnAdministratorCannotAttestASystemDerivedControl(string controlId)
    {
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("root-admin", "Administrator"));
        var command = new SubmitReadinessControlAttestationCommand(
            NewScope(), new ReadinessControlId(controlId), ReadinessControlStatus.Pass,
            "I am confident this passed", ReasonCode: string.Empty, CorrelationId.New());

        await Assert.ThrowsAsync<ProductionReadinessAttestationNotAllowedException>(() => useCase.ExecuteAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task IdenticalAttestationsConvergeToTheSameVersion()
    {
        var store = new InMemoryReadinessControlAttestationStore();
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Approver"), store);
        var scope = NewScope();
        var command = new SubmitReadinessControlAttestationCommand(
            scope, new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessControlStatus.Pass,
            "ADR-0031 approved 2026-08-15", ReasonCode: string.Empty, CorrelationId.New());

        var first = await useCase.ExecuteAsync(command, CancellationToken.None);
        var second = await useCase.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(first.AttestationVersion, second.AttestationVersion);
    }
}
