using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Waves;

/// <summary>Identidade de uma onda de migração.</summary>
public readonly record struct WaveId(Guid Value)
{
    /// <summary>Gera uma nova identidade de onda.</summary>
    public static WaveId New() => new(Guid.NewGuid());
}

/// <summary>
/// Agregado raiz de uma onda de migração. Pertence sempre a um projeto (tenant + project). A seleção
/// é versionada com hash determinístico: uma alteração cria nova versão e devolve a onda a Draft.
/// Após a aprovação, seleção, destino e <see cref="Waves.TargetRootFolder"/> são congelados
/// (imutáveis) e um retry preserva o mesmo destino. A onda não pode ser aprovada com validações
/// pendentes. Transições inválidas lançam <see cref="InvalidWaveTransitionException"/>.
/// </summary>
public sealed class MigrationWave
{
    private MigrationWave(
        WaveId id,
        TenantId tenant,
        ProjectId project,
        WaveName name,
        TargetRootFolder targetRootFolder,
        Sha256Hash configurationHash,
        WaveSelection selection,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Name = name;
        TargetRootFolder = targetRootFolder;
        ConfigurationHash = configurationHash;
        Selection = selection;
        Version = WaveVersion.Initial;
        Status = WaveStatus.Draft;
        CreatedAtUtc = createdAtUtc;
        RowVersion = RowVersion.None;
    }

    public WaveId Id { get; }

    public TenantId Tenant { get; }

    public ProjectId Project { get; }

    public WaveName Name { get; private set; }

    public WaveVersion Version { get; private set; }

    public WaveStatus Status { get; private set; }

    public TargetRootFolder TargetRootFolder { get; private set; }

    /// <summary>Hash da versão de configuração do projeto que esta onda tem como alvo.</summary>
    public Sha256Hash ConfigurationHash { get; }

    public WaveSelection Selection { get; private set; }

    /// <summary>Total planejado de bytes (derivado da seleção).</summary>
    public long PlannedBytes => Selection.TotalBytes;

    /// <summary>Total planejado de itens (derivado da seleção).</summary>
    public long PlannedItems => Selection.TotalItems;

    /// <summary>Hash determinístico da seleção corrente.</summary>
    public Sha256Hash SelectionHash => Selection.ComputeHash();

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Token de concorrência otimista carregado na leitura (conferido em cada UPDATE).</summary>
    public RowVersion RowVersion { get; private set; }

    /// <summary>Registra o token de concorrência atribuído pela persistência (uso do store).</summary>
    public void ApplyPersistedRowVersion(RowVersion rowVersion) => RowVersion = rowVersion;

    /// <summary>Cria uma onda nova em Draft (versão 1).</summary>
    public static MigrationWave Create(
        WaveId id,
        TenantId tenant,
        ProjectId project,
        WaveName name,
        TargetRootFolder targetRootFolder,
        Sha256Hash configurationHash,
        WaveSelection selection,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new MigrationWave(id, tenant, project, name, targetRootFolder, configurationHash, selection, now);
    }

    /// <summary>Reidrata a onda a partir do estado persistido.</summary>
    public static MigrationWave Rehydrate(
        WaveId id,
        TenantId tenant,
        ProjectId project,
        WaveName name,
        TargetRootFolder targetRootFolder,
        Sha256Hash configurationHash,
        WaveSelection selection,
        WaveVersion version,
        WaveStatus status,
        DateTimeOffset? approvedAtUtc,
        string? approvedBy,
        DateTimeOffset createdAtUtc,
        RowVersion rowVersion)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new MigrationWave(id, tenant, project, name, targetRootFolder, configurationHash, selection, createdAtUtc)
        {
            Version = version,
            Status = status,
            ApprovedAtUtc = approvedAtUtc,
            ApprovedBy = approvedBy,
            RowVersion = rowVersion,
        };
    }

    /// <summary>Altera a seleção: cria nova versão e devolve a onda a Draft. Congelada após aprovação.</summary>
    public void UpdateSelection(WaveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        EnsureSelectionMutable();
        Selection = selection;
        Version = Version.Next();
        Status = WaveStatus.Draft;
    }

    /// <summary>Altera a pasta de destino (permitida antes da aprovação; congelada depois).</summary>
    public void ChangeTargetRootFolder(TargetRootFolder targetRootFolder)
    {
        EnsureSelectionMutable();
        TargetRootFolder = targetRootFolder;
    }

    /// <summary>Renomeia a onda (permitido antes da aprovação).</summary>
    public void Rename(WaveName name)
    {
        EnsureSelectionMutable();
        Name = name;
    }

    /// <summary>Inicia a validação (Draft → Validating).</summary>
    public void StartValidation() => Transition(WaveStatus.Validating);

    /// <summary>Bloqueia por avaliação/capacidade (Validating → Blocked).</summary>
    public void Block() => Transition(WaveStatus.Blocked);

    /// <summary>Retoma a validação após resolução (Blocked → Validating).</summary>
    public void Unblock() => Transition(WaveStatus.Validating);

    /// <summary>Conclui as validações (Validating → ReadyForApproval).</summary>
    public void MarkReadyForApproval() => Transition(WaveStatus.ReadyForApproval);

    /// <summary>
    /// Aprova a onda (ReadyForApproval → Approved) e congela seleção, destino e pasta. Não é possível
    /// aprovar com validações pendentes (o estado só chega a ReadyForApproval após elas).
    /// </summary>
    public void Approve(string approvedBy, DateTimeOffset now)
    {
        var approver = TextValueOrThrow(approvedBy);
        Transition(WaveStatus.Approved);
        ApprovedBy = approver;
        ApprovedAtUtc = now;
    }

    /// <summary>Rejeita a onda na aprovação (ReadyForApproval → Draft), devolvendo-a para retrabalho.</summary>
    public void Reject() => Transition(WaveStatus.Draft);

    /// <summary>Congela a onda para execução (Approved → Frozen).</summary>
    public void Freeze() => Transition(WaveStatus.Frozen);

    /// <summary>Conclui a onda (Frozen → Completed).</summary>
    public void Complete() => Transition(WaveStatus.Completed);

    /// <summary>Cancela a onda (qualquer estado não terminal → Cancelled).</summary>
    public void Cancel() => Transition(WaveStatus.Cancelled);

    private static string TextValueOrThrow(string approvedBy)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new ArgumentException("approvedBy é obrigatório para aprovar a onda.", nameof(approvedBy));
        }

        return approvedBy.Trim();
    }

    private void Transition(WaveStatus to)
    {
        WaveStateMachine.EnsureCanTransition(Status, to);
        Status = to;
    }

    private void EnsureSelectionMutable()
    {
        if (!WaveStateMachine.IsSelectionMutable(Status))
        {
            throw new InvalidWaveTransitionException(
                $"A seleção/destino da onda está congelada no estado {Status}; não pode ser alterada silenciosamente.");
        }
    }
}
