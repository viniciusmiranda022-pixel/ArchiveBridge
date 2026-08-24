using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Pedido lógico DURÁVEL de upload Purview de UMA wave (AB-I5-009 item 8) — o vínculo estável entre a
/// onda e o <see cref="Jobs.JobId"/> durável (workload <c>Upload</c>) que reivindica/executa/reintenta o
/// transporte. NUNCA carrega o conjunto de bindings, o SAS, o binário ou evidência — apenas a IDENTIDADE do
/// pedido; o conteúdo de CADA tentativa (incluindo a identidade lógica calculada naquele instante, item 14)
/// vive em <c>PurviewUploadAttemptRecord</c> (Contracts), append-only. Um único pedido por (tenant, projeto,
/// wave), para sempre — retry/restart do worker reutilizam o MESMO pedido e Job, nunca criam um segundo.
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <c>PurviewSasUploadHandle</c>/
/// <c>WavePartitionOutputBinding</c>): <see cref="Rehydrate"/> recomputa <see cref="RequestHash"/> a partir
/// dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record PurviewUploadRequest
{
    private PurviewUploadRequest(
        PurviewUploadRequestId id, TenantId tenant, ProjectId project, WaveId wave, JobId job,
        CorrelationId correlation, DateTimeOffset createdAtUtc, Sha256Hash requestHash)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Wave = wave;
        Job = job;
        Correlation = correlation;
        CreatedAtUtc = createdAtUtc;
        RequestHash = requestHash;
    }

    /// <summary>Cria um novo pedido lógico, vinculado ao Job durável recém-criado para o mesmo escopo.</summary>
    /// <exception cref="ArgumentException">Tenant/projeto ausentes.</exception>
    public static PurviewUploadRequest Create(
        PurviewUploadRequestId id, TenantId tenant, ProjectId project, WaveId wave, JobId job,
        CorrelationId correlation, DateTimeOffset nowUtc)
    {
        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant é obrigatório.", nameof(tenant));
        }

        if (project.Value == Guid.Empty)
        {
            throw new ArgumentException("Projeto é obrigatório.", nameof(project));
        }

        var canonicalNowUtc = TruncateToMilliseconds(nowUtc);
        var hash = ComputeRequestHash(id, tenant, project, wave, job, correlation, canonicalNowUtc);
        return new PurviewUploadRequest(id, tenant, project, wave, job, correlation, canonicalNowUtc, hash);
    }

    /// <summary>
    /// Reconstrói um pedido JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="RequestHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="PurviewUploadIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static PurviewUploadRequest Rehydrate(
        PurviewUploadRequestId id, TenantId tenant, ProjectId project, WaveId wave, JobId job,
        CorrelationId correlation, DateTimeOffset createdAtUtc, Sha256Hash persistedRequestHash)
    {
        var recomputed = ComputeRequestHash(id, tenant, project, wave, job, correlation, createdAtUtc);
        if (!string.Equals(recomputed.Value, persistedRequestHash.Value, StringComparison.Ordinal))
        {
            throw new PurviewUploadIntegrityViolationException(
                $"O request_hash persistido para {id.Value} não corresponde ao hash recomputado a partir dos " +
                "campos carregados — pedido de upload possivelmente adulterado ou corrompido.");
        }

        return new PurviewUploadRequest(id, tenant, project, wave, job, correlation, createdAtUtc, persistedRequestHash);
    }

    /// <summary>Identidade do pedido.</summary>
    public PurviewUploadRequestId Id { get; }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda cujo upload este pedido representa. Um pedido por (tenant, projeto, wave), para sempre.</summary>
    public WaveId Wave { get; }

    /// <summary>Job durável (workload <c>Upload</c>) que reivindica/executa/reintenta este pedido.</summary>
    public JobId Job { get; }

    /// <summary>Correlação da criação do pedido (auditoria).</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante de criação (append-only — nunca mutado depois).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Hash determinístico de todos os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash RequestHash { get; }

    private static Sha256Hash ComputeRequestHash(
        PurviewUploadRequestId id, TenantId tenant, ProjectId project, WaveId wave, JobId job,
        CorrelationId correlation, DateTimeOffset createdAtUtc) =>
        DeterministicHash.Compute(
        [
            id.Value.ToString("N"),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            job.Value.ToString("N"),
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(createdAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
