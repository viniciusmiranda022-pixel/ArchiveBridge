using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>Identidade de uma tentativa de inspeção, gerada pelo servidor.</summary>
public readonly record struct InspectionId(Guid Value)
{
    /// <summary>Gera uma nova identidade de tentativa.</summary>
    public static InspectionId New() => new(Guid.NewGuid());
}

/// <summary>
/// Checkpoint imutável de UMA tentativa de inspeção de PST (Slice 4B, Passo 1). Append-only — nunca
/// atualizado após persistido; cada nova tentativa é um novo registro. Uma tentativa <c>Completed</c> com
/// hash observado igual ao <see cref="MigrationArtifact.RegisteredHash"/> é a única que pode se tornar o
/// resultado CANÔNICO reaproveitável em réplay idempotente (§4 dos critérios de aceite); <c>Stale</c> e
/// <c>LimitExceeded</c> nunca são canônicos — existem apenas como evidência/auditoria.
/// </summary>
public sealed class PstInspectionRecord
{
    private PstInspectionRecord(
        InspectionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        Sha256Hash expectedHash,
        Sha256Hash? observedHash,
        long? observedSizeBytes,
        PstInspectionOutcome outcome,
        PstStructuralDiagnostic? diagnostic,
        PstFormatVariant? formatVariant,
        string engineName,
        string engineVersion,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Artifact = artifact;
        ExpectedHash = expectedHash;
        ObservedHash = observedHash;
        ObservedSizeBytes = observedSizeBytes;
        Outcome = outcome;
        Diagnostic = diagnostic;
        FormatVariant = formatVariant;
        EngineName = engineName;
        EngineVersion = engineVersion;
        Correlation = correlation;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    /// <summary>
    /// Tentativa concluída: a engine chegou a um diagnóstico estrutural definitivo. Hash/tamanho
    /// observados são <c>null</c> APENAS quando <paramref name="diagnostic"/> é
    /// <see cref="PstStructuralDiagnostic.ReadError"/> (o arquivo não pôde ser lido por completo — nenhum
    /// hash confiável existe); em qualquer outro diagnóstico a engine leu o arquivo inteiro e ambos são
    /// obrigatórios. Só é elegível a canônica quando o hash observado bate com o registrado em custódia
    /// (ver <see cref="IsCanonical"/>) — divergência de hash é modelada por <see cref="Stale"/>, não aqui.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Hash/tamanho ausentes com diagnóstico diferente de <see cref="PstStructuralDiagnostic.ReadError"/>,
    /// ou presentes com diagnóstico <see cref="PstStructuralDiagnostic.ReadError"/>.
    /// </exception>
    public static PstInspectionRecord Complete(
        InspectionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        Sha256Hash expectedHash,
        Sha256Hash? observedHash,
        long? observedSizeBytes,
        PstStructuralDiagnostic diagnostic,
        PstFormatVariant formatVariant,
        string engineName,
        string engineVersion,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        RequireEngine(engineName, engineVersion);
        RequireTimestamps(startedAtUtc, completedAtUtc);

        var hashPresent = observedHash is not null && observedSizeBytes is not null;
        if (diagnostic == PstStructuralDiagnostic.ReadError && hashPresent)
        {
            throw new ArgumentException(
                "Diagnóstico ReadError nunca tem hash/tamanho observados (leitura não concluída).", nameof(diagnostic));
        }

        if (diagnostic != PstStructuralDiagnostic.ReadError && !hashPresent)
        {
            throw new ArgumentException(
                "Diagnóstico diferente de ReadError exige hash e tamanho observados (leitura completa).", nameof(diagnostic));
        }

        return new PstInspectionRecord(
            id, tenant, project, artifact, expectedHash, observedHash, observedSizeBytes,
            PstInspectionOutcome.Completed, diagnostic, formatVariant, engineName, engineVersion,
            correlation, startedAtUtc, completedAtUtc);
    }

    /// <summary>Tentativa bloqueada fail-closed: o hash observado diverge do registrado em custódia
    /// (artefato alterado desde o registro). Nunca canônica — nunca reutilizada como sucesso.</summary>
    public static PstInspectionRecord Stale(
        InspectionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        Sha256Hash expectedHash,
        Sha256Hash observedHash,
        long observedSizeBytes,
        string engineName,
        string engineVersion,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        RequireEngine(engineName, engineVersion);
        RequireTimestamps(startedAtUtc, completedAtUtc);
        return new PstInspectionRecord(
            id, tenant, project, artifact, expectedHash, observedHash, observedSizeBytes,
            PstInspectionOutcome.Stale, diagnostic: null, formatVariant: null, engineName, engineVersion,
            correlation, startedAtUtc, completedAtUtc);
    }

    /// <summary>Tentativa interrompida fail-closed por limite de tamanho/tempo/recursos ou cancelamento
    /// do servidor. Nunca canônica. Hash/tamanho observados podem ser desconhecidos (leitura não concluída).</summary>
    public static PstInspectionRecord LimitExceeded(
        InspectionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        Sha256Hash expectedHash,
        long? observedSizeBytes,
        string engineName,
        string engineVersion,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        RequireEngine(engineName, engineVersion);
        RequireTimestamps(startedAtUtc, completedAtUtc);
        return new PstInspectionRecord(
            id, tenant, project, artifact, expectedHash, observedHash: null, observedSizeBytes,
            PstInspectionOutcome.LimitExceeded, diagnostic: null, formatVariant: null, engineName, engineVersion,
            correlation, startedAtUtc, completedAtUtc);
    }

    /// <summary>Reconstrói uma tentativa já persistida (uso exclusivo da camada de persistência).</summary>
    public static PstInspectionRecord Rehydrate(
        InspectionId id,
        TenantId tenant,
        ProjectId project,
        ArtifactId artifact,
        Sha256Hash expectedHash,
        Sha256Hash? observedHash,
        long? observedSizeBytes,
        PstInspectionOutcome outcome,
        PstStructuralDiagnostic? diagnostic,
        PstFormatVariant? formatVariant,
        string engineName,
        string engineVersion,
        CorrelationId correlation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc) =>
        new(id, tenant, project, artifact, expectedHash, observedHash, observedSizeBytes, outcome,
            diagnostic, formatVariant, engineName, engineVersion, correlation, startedAtUtc, completedAtUtc);

    private static void RequireEngine(string engineName, string engineVersion)
    {
        TextValue.Require(engineName, nameof(engineName), 100);
        TextValue.Require(engineVersion, nameof(engineVersion), 50);
    }

    private static void RequireTimestamps(DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc)
    {
        if (completedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("completedAtUtc não pode ser anterior a startedAtUtc.", nameof(completedAtUtc));
        }
    }

    /// <summary>Identidade da tentativa.</summary>
    public InspectionId Id { get; }

    /// <summary>Tenant do escopo autorizado (nunca inferido do cliente).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado (nunca inferido do cliente).</summary>
    public ProjectId Project { get; }

    /// <summary>Artefato inspecionado.</summary>
    public ArtifactId Artifact { get; }

    /// <summary>Hash registrado em custódia no momento da requisição (baseline de staleness).</summary>
    public Sha256Hash ExpectedHash { get; }

    /// <summary>Hash observado nesta tentativa (nulo apenas em <see cref="PstInspectionOutcome.LimitExceeded"/> sem leitura concluída).</summary>
    public Sha256Hash? ObservedHash { get; }

    /// <summary>Tamanho observado nesta tentativa, em bytes.</summary>
    public long? ObservedSizeBytes { get; }

    /// <summary>Desfecho da tentativa.</summary>
    public PstInspectionOutcome Outcome { get; }

    /// <summary>Diagnóstico estrutural — presente apenas quando <see cref="Outcome"/> é <see cref="PstInspectionOutcome.Completed"/>.</summary>
    public PstStructuralDiagnostic? Diagnostic { get; }

    /// <summary>Variante de formato — presente apenas quando <see cref="Outcome"/> é <see cref="PstInspectionOutcome.Completed"/>.</summary>
    public PstFormatVariant? FormatVariant { get; }

    /// <summary>Nome do adapter/engine que executou a tentativa (evidência/auditoria).</summary>
    public string EngineName { get; }

    /// <summary>Versão do adapter/engine que executou a tentativa (evidência/auditoria).</summary>
    public string EngineVersion { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Início da tentativa (UTC).</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Conclusão da tentativa (UTC).</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>
    /// Verdadeiro quando esta tentativa é elegível a canônica: concluída com sucesso estrutural E hash
    /// observado igual ao registrado (nenhuma divergência de custódia). Uma tentativa canônica é a única
    /// que a store pode reter sob o índice único filtrado de idempotência.
    /// </summary>
    public bool IsCanonical =>
        Outcome == PstInspectionOutcome.Completed && ObservedHash is { } observed && observed == ExpectedHash;
}
