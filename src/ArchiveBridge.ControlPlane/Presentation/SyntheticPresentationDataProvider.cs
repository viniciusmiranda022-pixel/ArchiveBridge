using System.Security.Cryptography;
using System.Text;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Implementação em memória do <see cref="IPresentationDataProvider"/> com um dataset 100% SINTÉTICO
/// ("Contoso Demo"). Todos os nomes, hashes e volumes são fictícios; nenhum cliente, e-mail, PST ou tenant
/// real aparece. É estritamente READ-ONLY: não escreve em SQL, não toca filesystem, não enfileira Job nem
/// invoca Worker/PowerShell. Os identificadores são estáveis (GUIDs fixos) para que os links de detalhe
/// resolvam dentro do próprio dataset. Os instantes são ancorados em "agora" (via <see cref="IClock"/>) para
/// parecerem recentes numa demonstração.
/// </summary>
public sealed class SyntheticPresentationDataProvider(IClock clock) : IPresentationDataProvider
{
    // Identidades estáveis do dataset de demonstração.
    private static readonly Guid Project1 = new("a1b2c3d4-0000-4000-8000-000000000101");
    private static readonly Guid Project2 = new("a1b2c3d4-0000-4000-8000-000000000102");
    private static readonly Guid Project3 = new("a1b2c3d4-0000-4000-8000-000000000103");

    private static readonly Guid Env1 = new("e0000000-0000-4000-8000-0000000000e1");
    private static readonly Guid Env2 = new("e0000000-0000-4000-8000-0000000000e2");

    private static readonly Guid WaveExec = new("bb000000-0000-4000-8000-000000000001");
    private static readonly Guid WaveFin = new("bb000000-0000-4000-8000-000000000002");
    private static readonly Guid WaveOps = new("bb000000-0000-4000-8000-000000000003");
    private static readonly Guid WaveLegal = new("bb000000-0000-4000-8000-000000000004");
    private static readonly Guid WaveMkt = new("bb000000-0000-4000-8000-000000000005");
    private static readonly Guid WaveIt = new("bb000000-0000-4000-8000-000000000006");
    private static readonly Guid WaveSales = new("bb000000-0000-4000-8000-000000000007");

    private static readonly Guid JobProcessing = new("cc000000-0000-4000-8000-000000000001");
    private static readonly Guid JobPending = new("cc000000-0000-4000-8000-000000000002");
    private static readonly Guid JobRetry = new("cc000000-0000-4000-8000-000000000003");
    private static readonly Guid JobDone = new("cc000000-0000-4000-8000-000000000004");
    private static readonly Guid JobFailed = new("cc000000-0000-4000-8000-000000000005");

    private static readonly Guid UserOperator = new("d0000000-0000-4000-8000-0000000000a1");
    private static readonly Guid UserAuditor = new("d0000000-0000-4000-8000-0000000000a2");

    private const long Gb = 1_000_000_000L;

    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public PresentationScope Scope { get; } = new("Contoso Demo", "Migração Enterprise Vault → Microsoft 365");

    /// <inheritdoc />
    public DashboardViewModel GetDashboard()
    {
        var now = _clock.UtcNow;

        // Contagens DERIVADAS das coleções sintéticas para que os números se falem entre as telas
        // (o painel nunca contradiz as listas de Projetos/Ondas/Ambientes/Jobs/Evidências).
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var jobsRunning = GetJobs().Count(job => string.Equals(job.State, "Processing", StringComparison.Ordinal));
        var metrics = new List<DashboardMetric>
        {
            new("projects", "Projetos", GetProjects().Count.ToString(inv), true),
            new("waves", "Ondas", GetWaves().Count.ToString(inv), true),
            new("environments", "Ambientes EV", GetEnvironments().Count.ToString(inv), true),
            new("jobs", "Jobs em execução", jobsRunning.ToString(inv), true),
            new("evidence", "Evidências", GetEvidence().Count.ToString(inv), true),
            // Validações de CSV acumuladas — métrica isolada (sem lista correspondente que possa contradizê-la).
            new("validations", "Validações", "12", true),
        };

        var pipeline = BuildPipeline(active: 4);

        var platform = new List<PlatformComponentStatus>
        {
            new("Control Plane", "Portal operacional on-premises", "success", "Operacional"),
            new("SQL Server", "Persistência durável e RLS por tenant", "success", "Conectado"),
            new("Enterprise Vault Worker", "Descoberta de capacidades (read-only)", "success", "Operacional"),
            new("Evidence Store", "Cadeia de custódia imutável", "success", "Disponível"),
            new("Microsoft 365 Connector", "Importação — etapa futura", "neutral", "Não configurado"),
        };

        var activity = new List<ActivityEntry>
        {
            new(now.AddMinutes(-6), "Discovery concluída", "EV-SITE-01", true, "operator.demo"),
            new(now.AddMinutes(-24), "Evidência baixada", "EV-SITE-01 / v3", true, "auditor.demo"),
            new(now.AddMinutes(-58), "Onda aprovada", "Wave Financeiro", true, "approver.demo"),
            new(now.AddHours(-2), "Discovery solicitada", "EV-SITE-02", true, "operator.demo"),
            new(now.AddHours(-3), "Discovery bloqueada", "EV-SITE-02", false, "operator.demo"),
            new(now.AddHours(-5), "Login", "portal", true, "auditor.demo"),
        };

        return new DashboardViewModel(metrics, pipeline, platform, activity);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProjectSummary> GetProjects()
    {
        var now = _clock.UtcNow;
        return new List<ProjectSummary>
        {
            new(Project1, "Migração Enterprise Vault → Microsoft 365", "operations@contoso-demo", "contoso-demo.onmicrosoft.com", 7, "Active", now.AddHours(-3)),
            new(Project2, "Arquivamento Jurídico — Retenção 10 anos", "legal@contoso-demo", "contoso-demo.onmicrosoft.com", 4, "Approved", now.AddDays(-2)),
            new(Project3, "Descomissionamento EV — Filiais", "infra@contoso-demo", "contoso-demo.onmicrosoft.com", 1, "Draft", now.AddDays(-9)),
        };
    }

    /// <inheritdoc />
    public ProjectOverview? GetProject(Guid projectId)
    {
        var project = GetProjects().FirstOrDefault(p => p.ProjectId == projectId);
        if (project is null)
        {
            return null;
        }

        var waves = GetWaves().Where(w => w.ProjectId == projectId).ToList();
        var jobs = GetJobs().Where(j => j.ProjectId == projectId).ToList();
        return new ProjectOverview(
            project.ProjectId,
            project.Name,
            project.Status,
            project.TargetTenant,
            "Retenção padrão · caixas de arquivo online",
            project.ConfigurationVersion,
            Hash($"config-{projectId}"),
            project.UpdatedAtUtc,
            waves,
            projectId == Project1 ? GetEnvironments() : [],
            jobs);
    }

    /// <inheritdoc />
    public IReadOnlyList<WaveSummary> GetWaves()
    {
        var now = _clock.UtcNow;
        return new List<WaveSummary>
        {
            new(WaveExec, Project1, "Wave Executivos", 3, "Frozen", 128L * Gb, 412_300, now.AddDays(-6), now.AddDays(-5), "approver.demo"),
            new(WaveFin, Project1, "Wave Financeiro", 2, "Approved", 1_820L * Gb, 3_204_880, now.AddDays(-4), now.AddDays(-1), "approver.demo"),
            new(WaveOps, Project1, "Wave Operações", 1, "ReadyForApproval", 640L * Gb, 1_150_400, now.AddDays(-3), null, null),
            new(WaveLegal, Project1, "Wave Jurídico", 1, "Validating", 305L * Gb, 902_115, now.AddDays(-2), null, null),
            new(WaveMkt, Project1, "Wave Marketing", 1, "Draft", 74L * Gb, 210_540, now.AddDays(-1), null, null),
            new(WaveIt, Project1, "Wave TI", 2, "Blocked", 512L * Gb, 1_004_200, now.AddDays(-7), null, null),
            new(WaveSales, Project1, "Wave Vendas", 4, "Completed", 96L * Gb, 350_770, now.AddDays(-12), now.AddDays(-10), "approver.demo"),
        };
    }

    /// <inheritdoc />
    public WaveDetail? GetWave(Guid waveId)
    {
        var wave = GetWaves().FirstOrDefault(w => w.WaveId == waveId);
        if (wave is null)
        {
            return null;
        }

        var lifecycle = WaveLifecycle.Build(wave.Status);
        var pstCount = (int)Math.Max(1, wave.PlannedBytes / (48L * Gb));
        var mappingStatus = wave.Status switch
        {
            "Frozen" or "Approved" or "Completed" => "Congelado",
            "ReadyForApproval" or "Validating" => "Gerado",
            _ => "Pendente",
        };

        return new WaveDetail(
            wave.WaveId,
            wave.Name,
            wave.WaveVersion,
            wave.Status,
            wave.PlannedBytes,
            wave.PlannedItems,
            pstCount,
            "/Arquivos migrados/" + wave.Name.Replace("Wave ", string.Empty, StringComparison.Ordinal),
            mappingStatus,
            wave.ApprovedBy,
            wave.CreatedAtUtc,
            lifecycle);
    }

    /// <inheritdoc />
    public IReadOnlyList<JobSummary> GetJobs()
    {
        var now = _clock.UtcNow;
        return new List<JobSummary>
        {
            new(JobProcessing, Project1, "ev-discovery", "Processing", 1, 100, "ev-worker-01", null, now.AddMinutes(-4)),
            new(JobPending, Project1, "ev-discovery", "Pending", 0, 100, null, null, now.AddMinutes(-2)),
            new(JobRetry, Project1, "ev-discovery", "RetryScheduled", 2, 120, null, "EV_TRANSIENT", now.AddMinutes(-15)),
            new(JobDone, Project1, "ev-discovery", "Completed", 1, 100, "ev-worker-01", null, now.AddHours(-1)),
            new(JobFailed, Project1, "ev-discovery", "Failed", 5, 100, null, "EV_PERMISSION_DENIED", now.AddHours(-3)),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<JobTransitionView> GetJobTransitions(Guid jobId)
    {
        var job = GetJobs().FirstOrDefault(j => j.JobId == jobId);
        if (job is null)
        {
            return [];
        }

        var start = job.UpdatedAtUtc.AddMinutes(-12);
        var transitions = new List<JobTransitionView>
        {
            new(1, null, "Pending", "enqueued", 0, null, start),
            new(2, "Pending", "Processing", "claimed", 1, "ev-worker-01", start.AddMinutes(3)),
        };

        switch (job.State)
        {
            case "Completed":
                transitions.Add(new JobTransitionView(3, "Processing", "Completed", "succeeded", 1, "ev-worker-01", start.AddMinutes(9)));
                break;
            case "Failed":
                transitions.Add(new JobTransitionView(3, "Processing", "RetryScheduled", "transient-error", 1, "ev-worker-01", start.AddMinutes(6)));
                transitions.Add(new JobTransitionView(4, "RetryScheduled", "Failed", "exhausted-retries", 5, null, start.AddMinutes(11)));
                break;
            case "RetryScheduled":
                transitions.Add(new JobTransitionView(3, "Processing", "RetryScheduled", "transient-error", 2, null, start.AddMinutes(8)));
                break;
            default:
                break;
        }

        return transitions;
    }

    /// <inheritdoc />
    public IReadOnlyList<EvEnvironmentSummary> GetEnvironments()
    {
        var now = _clock.UtcNow;
        var d1 = new EvDiscoverySummary(
            3, "Ready", "Ready", "EV_READY", "Get-EVArchive", 1, "15.1",
            Hash("cfg-e1"), Hash("sem-e1"), Hash("sha-e1-v3"), "ev/site-01/v3", 8_452, now.AddMinutes(-6));
        var d2 = new EvDiscoverySummary(
            2, "Blocked", "Blocked", "EV_PERMISSION_DENIED", null, null, "15.1",
            Hash("cfg-e2"), Hash("sem-e2"), Hash("sha-e2-v2"), "ev/site-02/v2", 6_119, now.AddHours(-3));

        return new List<EvEnvironmentSummary>
        {
            new(Env1, "EV-SITE-01", "evdir-01.contoso-demo.local", d1),
            new(Env2, "EV-SITE-02", "evdir-02.contoso-demo.local", d2),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<EvEvidenceListItem> GetEvidence()
    {
        var now = _clock.UtcNow;
        return new List<EvEvidenceListItem>
        {
            new(Env1, "EV-SITE-01", 3, "Ready", "Ready", "EV_READY", Hash("cfg-e1"), Hash("sem-e1"), Hash("sha-e1-v3"), "ev/site-01/v3", 8_452, now.AddMinutes(-6)),
            new(Env1, "EV-SITE-01", 2, "Superseded", "Ready", "EV_READY", Hash("cfg-e1"), Hash("sem-e1b"), Hash("sha-e1-v2"), "ev/site-01/v2", 8_401, now.AddDays(-2)),
            new(Env1, "EV-SITE-01", 1, "Superseded", "Unsupported", "EV_VERSION_UNSUPPORTED", Hash("cfg-e1"), Hash("sem-e1c"), Hash("sha-e1-v1"), "ev/site-01/v1", 7_980, now.AddDays(-8)),
            new(Env2, "EV-SITE-02", 2, "Blocked", "Blocked", "EV_PERMISSION_DENIED", Hash("cfg-e2"), Hash("sem-e2"), Hash("sha-e2-v2"), "ev/site-02/v2", 6_119, now.AddHours(-3)),
            new(Env2, "EV-SITE-02", 1, "Superseded", "Failed", "EV_PROBE_FAILED", Hash("cfg-e2"), Hash("sem-e2b"), Hash("sha-e2-v1"), "ev/site-02/v1", 5_902, now.AddDays(-4)),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<PortalOperationalAuditEvent> GetOperationalAudit()
    {
        var now = _clock.UtcNow;
        return new List<PortalOperationalAuditEvent>
        {
            new(UserOperator, "operator.demo", "ev.discovery.request", "ev-environment", Env1.ToString("N"), true, "accepted", Guid.NewGuid(), now.AddMinutes(-6)),
            new(UserAuditor, "auditor.demo", "evidence.download", "ev-discovery-evidence", $"{Env1:N}/v3", true, "verified", Guid.NewGuid(), now.AddMinutes(-24)),
            new(UserOperator, "operator.demo", "ev.discovery.request", "ev-environment", Env2.ToString("N"), true, "accepted", Guid.NewGuid(), now.AddHours(-2)),
            new(UserOperator, "operator.demo", "ev.discovery.request", "ev-environment", Env2.ToString("N"), false, "idempotency-conflict", Guid.NewGuid(), now.AddHours(-2).AddSeconds(-4)),
            new(UserAuditor, "auditor.demo", "evidence.download", "ev-discovery-evidence", $"{Env2:N}/v2", false, "not-found-or-not-authorized", Guid.NewGuid(), now.AddHours(-4)),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<PortalSignInEvent> GetSignIns()
    {
        var now = _clock.UtcNow;
        return new List<PortalSignInEvent>
        {
            new(null, null, UserOperator, "operator.demo", true, "ok", "10.4.12.31", Guid.NewGuid(), now.AddMinutes(-32)),
            new(null, null, UserAuditor, "auditor.demo", true, "ok", "10.4.12.44", Guid.NewGuid(), now.AddHours(-5)),
            new(null, null, null, "unknown.user", false, "invalid-credentials", "10.4.19.7", Guid.NewGuid(), now.AddHours(-6)),
            new(null, null, UserOperator, "operator.demo", false, "invalid-credentials", "10.4.12.31", Guid.NewGuid(), now.AddHours(-6).AddMinutes(-1)),
            new(null, null, UserAuditor, "auditor.demo", true, "ok", "10.4.12.44", Guid.NewGuid(), now.AddDays(-1)),
        };
    }

    /// <inheritdoc />
    public MappingOverview GetMapping()
    {
        var now = _clock.UtcNow;
        var history = new List<MappingHistoryEntry>
        {
            new("v3", Hash("map-v3"), 412, "Congelado", now.AddDays(-5), "operator.demo"),
            new("v2", Hash("map-v2"), 408, "Substituído", now.AddDays(-9), "operator.demo"),
            new("v1", Hash("map-v1"), 395, "Substituído", now.AddDays(-14), "operator.demo"),
        };

        return new MappingOverview(
            true,
            "v3",
            Hash("map-v3"),
            412,
            "Congelado",
            now.AddDays(-5),
            "operator.demo",
            history,
            PortalUploadEnabled: false);
    }

    // Constrói o pipeline de 8 etapas. As etapas 1..(active-1) são concluídas; a etapa "active" está em
    // andamento; exportação/staging/importação/reconciliação NUNCA aparecem executáveis (planejado/indisponível).
    private static List<PipelineStage> BuildPipeline(int active) =>
        new()
        {
            Stage(1, "Descoberta", active, "Concluído", "Em andamento", "Pendente"),
            Stage(2, "Planejamento", active, "Concluído", "Em andamento", "Pendente"),
            Stage(3, "Mapping", active, "Concluído", "Em andamento", "Pendente"),
            Stage(4, "Validação", active, "Concluído", "Em andamento", "Pendente"),
            new(5, "Exportação", PipelineState.Planned, "Planejado — próxima etapa"),
            new(6, "Staging", PipelineState.Unavailable, "Não disponível nesta versão"),
            new(7, "Importação M365", PipelineState.Unavailable, "Não disponível nesta versão"),
            new(8, "Reconciliação", PipelineState.Planned, "Planejado — próxima etapa"),
        };

    private static PipelineStage Stage(int number, string name, int active, string doneLabel, string activeLabel, string pendingLabel)
    {
        if (number < active)
        {
            return new PipelineStage(number, name, PipelineState.Done, doneLabel);
        }

        return number == active
            ? new PipelineStage(number, name, PipelineState.Active, activeLabel)
            : new PipelineStage(number, name, PipelineState.Pending, pendingLabel);
    }

    // Linha do tempo do ciclo de vida de uma onda: Draft → Validação → Aprovação → Frozen → Execução.
    private static List<PipelineStage> BuildWaveLifecycle(string status)
    {
        var reached = status switch
        {
            "Draft" => 1,
            "Validating" => 2,
            "Blocked" => 2,
            "ReadyForApproval" => 3,
            "Approved" => 4,
            "Frozen" => 4,
            "Completed" => 5,
            _ => 1,
        };

        string[] names = ["Draft", "Validação", "Aprovação", "Frozen", "Execução"];
        var stages = new List<PipelineStage>();
        for (var i = 0; i < names.Length; i++)
        {
            var number = i + 1;
            PipelineState state;
            string label;
            if (status == "Blocked" && number == 2)
            {
                state = PipelineState.Blocked;
                label = "Bloqueado";
            }
            else if (number < reached)
            {
                state = PipelineState.Done;
                label = "Concluído";
            }
            else if (number == reached)
            {
                state = status == "Completed" ? PipelineState.Done : PipelineState.Active;
                label = status == "Completed" ? "Concluído" : "Etapa atual";
            }
            else
            {
                state = PipelineState.Pending;
                label = "Pendente";
            }

            stages.Add(new PipelineStage(number, names[i], state, label));
        }

        return stages;
    }

    // Hash sintético determinístico (64 hex) a partir de uma semente — apenas aparência de custódia.
    private static string Hash(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("contoso-demo:" + seed)));
}
