using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Approvals;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Slice 4A — o caso de uso de SOLICITAÇÃO resolve toda a autoridade SERVER-SIDE. Prova que o comando
/// enfileirado carrega site/directory do <see cref="IEvEnvironmentCatalog"/>, versão/hash de configuração do
/// <see cref="IProjectStore"/> e a versão da <see cref="EvDiscoveryPolicy"/> — nunca valores externos —, que
/// ambiente/projeto inexistentes falham fechado sem enfileirar, e que o solicitante é fail-closed/sanitizado.
/// </summary>
public sealed class RequestEvCapabilityDiscoveryUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly EvDiscoveryPolicy Policy = EvDiscoveryPolicy.Default;

    private static TenantScope NewScope() =>
        new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static MigrationProject NewProject(TenantScope scope) =>
        MigrationProject.Create(
            scope.Project,
            scope.Tenant,
            new ProjectName("Projeto"),
            new ProjectOwner("owner@contoso.com"),
            new ProjectConfiguration(new TargetTenant("contoso.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive),
            Now);

    private static RequestEvCapabilityDiscovery Request(TenantScope scope, EvEnvironmentId environment, string requestedBy) =>
        new(scope, environment, requestedBy, Guid.NewGuid(), CorrelationId.New());

    [Fact]
    public async Task ResolvesServerSideTargetsIgnoringAnyExternalValue()
    {
        var scope = NewScope();
        var environmentId = new EvEnvironmentId(Guid.NewGuid());
        var project = NewProject(scope);

        // Alvo AUTORITATIVO vindo do catálogo (SQL server-side), nunca do cliente.
        const string authoritativeSite = "auth-site";
        const string authoritativeDirectory = "auth-dir.contoso.local";
        var catalog = new FakeEvEnvironmentCatalog(new EvEnvironmentRecord(environmentId, authoritativeSite, authoritativeDirectory));
        var projects = new FakeProjectStore(project);
        var inbox = new CapturingInbox();
        var useCase = new RequestEvCapabilityDiscoveryUseCase(catalog, projects, inbox, Policy);

        // Valor "malicioso/falso" PREPARADO FORA do use case: jamais deve aparecer no comando enfileirado.
        var spoofedHash = new Sha256Hash(new string('f', 64));
        Assert.NotEqual(spoofedHash, project.ConfigurationHash);

        var result = await useCase.ExecuteAsync(Request(scope, environmentId, "operator@contoso.local"), CancellationToken.None);

        var command = inbox.Captured;
        Assert.NotNull(command);
        Assert.Equal(1, inbox.EnqueueCount);
        Assert.Equal(environmentId, command!.EnvironmentId);
        Assert.Equal(authoritativeSite, command.SiteName);                 // do IEvEnvironmentCatalog
        Assert.Equal(authoritativeDirectory, command.DirectoryServer);     // do IEvEnvironmentCatalog
        Assert.Equal(project.ConfigurationVersion.Value, command.Context.ExpectedProjectConfigurationVersion); // do IProjectStore
        Assert.Equal(project.ConfigurationHash, command.Context.ExpectedConfigurationHash!.Value);             // do IProjectStore
        Assert.NotEqual(spoofedHash, command.Context.ExpectedConfigurationHash!.Value);                        // nunca o valor externo
        Assert.Equal(Policy.PolicyVersion, command.Context.DiscoveryPolicyVersion);                            // da EvDiscoveryPolicy
        Assert.Equal(EvDiscoveryCommandContext.CurrentSchemaVersion, command.Context.SchemaVersion);
        Assert.True(result.Created);
    }

    [Fact]
    public async Task NonexistentEnvironmentFailsClosedWithoutEnqueue()
    {
        var scope = NewScope();
        var catalog = new FakeEvEnvironmentCatalog(environmentRecord: null); // fora do escopo ≡ inexistente
        var projects = new FakeProjectStore(NewProject(scope));
        var inbox = new CapturingInbox();
        var useCase = new RequestEvCapabilityDiscoveryUseCase(catalog, projects, inbox, Policy);

        await Assert.ThrowsAsync<EvDiscoveryNotFoundException>(() =>
            useCase.ExecuteAsync(Request(scope, new EvEnvironmentId(Guid.NewGuid()), "operator@contoso.local"), CancellationToken.None));

        Assert.Equal(0, inbox.EnqueueCount); // zero Job
    }

    [Fact]
    public async Task NonexistentProjectFailsClosedWithoutEnqueue()
    {
        var scope = NewScope();
        var environmentId = new EvEnvironmentId(Guid.NewGuid());
        var catalog = new FakeEvEnvironmentCatalog(new EvEnvironmentRecord(environmentId, "site", "dir.contoso.local"));
        var projects = new FakeProjectStore(project: null); // projeto inexistente no escopo
        var inbox = new CapturingInbox();
        var useCase = new RequestEvCapabilityDiscoveryUseCase(catalog, projects, inbox, Policy);

        await Assert.ThrowsAsync<EvDiscoveryNotFoundException>(() =>
            useCase.ExecuteAsync(Request(scope, environmentId, "operator@contoso.local"), CancellationToken.None));

        Assert.Equal(0, inbox.EnqueueCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")] // apenas caracteres de controle/whitespace
    public async Task RequestedByBlankOrControlOnlyFailsClosed(string requestedBy)
    {
        var scope = NewScope();
        var environmentId = new EvEnvironmentId(Guid.NewGuid());
        var catalog = new FakeEvEnvironmentCatalog(new EvEnvironmentRecord(environmentId, "site", "dir.contoso.local"));
        var projects = new FakeProjectStore(NewProject(scope));
        var inbox = new CapturingInbox();
        var useCase = new RequestEvCapabilityDiscoveryUseCase(catalog, projects, inbox, Policy);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(Request(scope, environmentId, requestedBy), CancellationToken.None));

        Assert.Equal(0, inbox.EnqueueCount);
    }

    [Fact]
    public async Task RequestedByValidIsPersistedSanitized()
    {
        var scope = NewScope();
        var environmentId = new EvEnvironmentId(Guid.NewGuid());
        var catalog = new FakeEvEnvironmentCatalog(new EvEnvironmentRecord(environmentId, "site", "dir.contoso.local"));
        var projects = new FakeProjectStore(NewProject(scope));
        var inbox = new CapturingInbox();
        var useCase = new RequestEvCapabilityDiscoveryUseCase(catalog, projects, inbox, Policy);

        // Whitespace de borda + caractere de controle embutido: higienizado (controle removido, bordas aparadas).
        await useCase.ExecuteAsync(Request(scope, environmentId, "  oper\tator@contoso.local  "), CancellationToken.None);

        Assert.Equal("operator@contoso.local", inbox.Captured!.RequestedBy);
    }

    [Fact]
    public async Task EmptyIdempotencyKeyFailsClosedWithoutEnqueue()
    {
        var scope = NewScope();
        var environmentId = new EvEnvironmentId(Guid.NewGuid());
        var catalog = new FakeEvEnvironmentCatalog(new EvEnvironmentRecord(environmentId, "site", "dir.contoso.local"));
        var projects = new FakeProjectStore(NewProject(scope));
        var inbox = new CapturingInbox();
        var useCase = new RequestEvCapabilityDiscoveryUseCase(catalog, projects, inbox, Policy);

        var request = new RequestEvCapabilityDiscovery(scope, environmentId, "operator@contoso.local", Guid.Empty, CorrelationId.New());
        await Assert.ThrowsAsync<ArgumentException>(() => useCase.ExecuteAsync(request, CancellationToken.None));

        Assert.Equal(0, inbox.EnqueueCount);
    }

    // ---- Fakes test-only das portas (sem SQL): apenas o comportamento observável pelo use case ----

    private sealed class FakeEvEnvironmentCatalog(EvEnvironmentRecord? environmentRecord) : IEvEnvironmentCatalog
    {
        public Task<EvEnvironmentRecord?> GetAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken) =>
            Task.FromResult(environmentRecord);
    }

    private sealed class FakeProjectStore(MigrationProject? project) : IProjectStore
    {
        private readonly MigrationProject? _project = project;

        public Task<MigrationProject?> GetAsync(TenantScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(_project);

        public Task AddAsync(MigrationProject value, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveStatusAsync(MigrationProject value, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) =>
            throw new NotSupportedException();

        public Task SaveConfigurationAsync(MigrationProject value, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveStatusWithApprovalAsync(MigrationProject value, ApprovalRecord approval, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingInbox : IEvDiscoveryCommandInbox
    {
        public EvDiscoveryCommand? Captured { get; private set; }

        public int EnqueueCount { get; private set; }

        public Task<EvDiscoveryEnqueueResult> EnqueueIdempotentAsync(
            EvDiscoveryCommand command, Guid idempotencyKey, CancellationToken cancellationToken)
        {
            Captured = command;
            EnqueueCount++;
            return Task.FromResult(new EvDiscoveryEnqueueResult(JobId.New(), Created: true, Replayed: false));
        }

        public Task<JobId> EnqueueAsync(EvDiscoveryCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClaimedEvDiscoveryCommand?> TryClaimNextAsync(
            TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
