using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;

namespace ArchiveBridge.Domain.Projects;

/// <summary>
/// Agregado raiz de um projeto de migração. Pertence sempre a um tenant; owner e destino são
/// obrigatórios. A configuração que afeta o resultado é versionada com hash determinístico: uma
/// alteração cria uma nova versão e devolve o projeto a Draft (nunca muda silenciosamente); uma
/// configuração aprovada é congelada (alterá-la falha de forma fechada). Não armazena segredo, SAS
/// nem token. Transições inválidas lançam <see cref="InvalidProjectTransitionException"/>.
/// </summary>
public sealed class MigrationProject
{
    private MigrationProject(
        ProjectId id,
        TenantId tenant,
        ProjectName name,
        ProjectOwner owner,
        ProjectConfiguration configuration,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Name = name;
        Owner = owner;
        Configuration = configuration;
        ConfigurationVersion = ConfigurationVersion.Initial;
        ConfigurationHash = configuration.ComputeHash();
        Status = ProjectStatus.Draft;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        RowVersion = RowVersion.None;
    }

    public ProjectId Id { get; }

    public TenantId Tenant { get; }

    public ProjectName Name { get; private set; }

    public ProjectOwner Owner { get; private set; }

    public ProjectConfiguration Configuration { get; private set; }

    public ConfigurationVersion ConfigurationVersion { get; private set; }

    public Sha256Hash ConfigurationHash { get; private set; }

    public ProjectStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Token de concorrência otimista carregado na leitura (conferido em cada UPDATE).</summary>
    public RowVersion RowVersion { get; private set; }

    /// <summary>Registra o token de concorrência atribuído pela persistência (uso do store).</summary>
    public void ApplyPersistedRowVersion(RowVersion rowVersion) => RowVersion = rowVersion;

    /// <summary>Cria um projeto novo em Draft, na configuração inicial (versão 1).</summary>
    public static MigrationProject Create(
        ProjectId id,
        TenantId tenant,
        ProjectName name,
        ProjectOwner owner,
        ProjectConfiguration configuration,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new MigrationProject(id, tenant, name, owner, configuration, now);
    }

    /// <summary>Reidrata o agregado a partir do estado persistido.</summary>
    public static MigrationProject Rehydrate(
        ProjectId id,
        TenantId tenant,
        ProjectName name,
        ProjectOwner owner,
        ProjectConfiguration configuration,
        ConfigurationVersion configurationVersion,
        Sha256Hash configurationHash,
        ProjectStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        RowVersion rowVersion)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new MigrationProject(id, tenant, name, owner, configuration, createdAtUtc)
        {
            ConfigurationVersion = configurationVersion,
            ConfigurationHash = configurationHash,
            Status = status,
            UpdatedAtUtc = updatedAtUtc,
            RowVersion = rowVersion,
        };
    }

    /// <summary>
    /// Altera a configuração que afeta o resultado: cria uma nova versão, recalcula o hash e devolve
    /// o projeto a Draft (obrigando nova avaliação). Bloqueado quando a configuração está congelada
    /// (Approved em diante) — fail-closed.
    /// </summary>
    public void UpdateConfiguration(ProjectConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureConfigurationMutable();
        Configuration = configuration;
        ConfigurationVersion = ConfigurationVersion.Next();
        ConfigurationHash = configuration.ComputeHash();
        Status = ProjectStatus.Draft;
        UpdatedAtUtc = now;
    }

    /// <summary>Renomeia o projeto (não é configuração de resultado; permitido em estados mutáveis).</summary>
    public void Rename(ProjectName name, DateTimeOffset now)
    {
        EnsureConfigurationMutable();
        Name = name;
        UpdatedAtUtc = now;
    }

    /// <summary>Troca o owner (permitido em estados mutáveis).</summary>
    public void ChangeOwner(ProjectOwner owner, DateTimeOffset now)
    {
        EnsureConfigurationMutable();
        Owner = owner;
        UpdatedAtUtc = now;
    }

    /// <summary>Submete o projeto para avaliação (Draft → ReadyForAssessment).</summary>
    public void SubmitForAssessment(DateTimeOffset now) => Transition(ProjectStatus.ReadyForAssessment, now);

    /// <summary>Bloqueia por avaliação (ReadyForAssessment → Blocked).</summary>
    public void Block(DateTimeOffset now) => Transition(ProjectStatus.Blocked, now);

    /// <summary>Desbloqueia após resolução (Blocked → ReadyForAssessment).</summary>
    public void Unblock(DateTimeOffset now) => Transition(ProjectStatus.ReadyForAssessment, now);

    /// <summary>Aprova o projeto (ReadyForAssessment → Approved); a configuração passa a ser congelada.</summary>
    public void Approve(DateTimeOffset now) => Transition(ProjectStatus.Approved, now);

    /// <summary>Ativa o projeto (Approved → Active).</summary>
    public void Activate(DateTimeOffset now) => Transition(ProjectStatus.Active, now);

    /// <summary>Conclui o projeto (Active → Completed).</summary>
    public void Complete(DateTimeOffset now) => Transition(ProjectStatus.Completed, now);

    /// <summary>Cancela o projeto (qualquer estado não terminal → Cancelled).</summary>
    public void Cancel(DateTimeOffset now) => Transition(ProjectStatus.Cancelled, now);

    private void Transition(ProjectStatus to, DateTimeOffset now)
    {
        ProjectStateMachine.EnsureCanTransition(Status, to);
        Status = to;
        UpdatedAtUtc = now;
    }

    private void EnsureConfigurationMutable()
    {
        if (!ProjectStateMachine.IsConfigurationMutable(Status))
        {
            throw new InvalidProjectTransitionException(
                $"A configuração do projeto está congelada no estado {Status}; não pode ser alterada silenciosamente.");
        }
    }
}
