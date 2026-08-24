using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Handle OPACO de custódia de um SAS do Purview Network Upload (work order AB-I5-004 item 8) — persistido
/// no SQL como METADADO apenas: estado, fingerprint não reversível, referência opaca ao secret store,
/// metadados canonicalizados NÃO secretos (host/container/expiry) e linkage de auditoria. O material
/// secreto em si NUNCA atravessa este tipo — só <see cref="SecretStoreReference"/> (ver
/// <see cref="PurviewSasIntakePolicy"/>/<c>ISecretStore</c>).
/// <para>
/// Ciclo de vida (item 9, revisado por AB-I5-006 item 3): <c>Stored -&gt; Available -&gt; Claimed -&gt;
/// Consumed | Expired -&gt; Destroyed</c>, aplicado pelos métodos de transição abaixo — cada um recusa
/// fail-closed uma transição fora da ordem permitida (<see cref="PurviewSasLifecycleException"/>).
/// <see cref="SasHandleState.Claimed"/> é uma reserva DURÁVEL de uso único com lease/fencing por época
/// (<see cref="ClaimEpoch"/>, mesmo padrão de <c>Job</c>/<see cref="LeaseEpoch"/>, ADR-0003): reivindicar
/// (<see cref="Claim"/>) incrementa a época; a finalização (<see cref="FinalizeClaim"/>) só é aceita sob a
/// MESMA época titular — um owner reassumido por <see cref="Reclaim"/> (lease expirado) nunca consegue
/// finalizar com a época antiga. <see cref="Destroy"/> é idempotente (item 9: transições determinísticas);
/// as demais não são.
/// </para>
/// <para>
/// <see cref="Generation"/> versiona o handle CANÔNICO de uma wave (item 15): um novo intake para a MESMA
/// wave cria uma nova geração e invalida (destrói) a anterior de forma explícita e auditável — nunca duas
/// gerações simultaneamente "vivas" (Stored/Available/Claimed/Consumed) para a mesma wave (item 16),
/// reforçado por índice único filtrado na Infrastructure.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <c>CapabilityEvidence</c>/
/// <c>MailboxPrecheckSnapshot</c>): <see cref="Rehydrate"/> recomputa <see cref="HandleHash"/> a partir dos
/// campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record PurviewSasUploadHandle
{
    private PurviewSasUploadHandle(
        SasHandleId id,
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int generation,
        SasHandleState state,
        Sha256Hash fingerprint,
        SecretStoreHandleReference secretStoreReference,
        string authorizedHost,
        string authorizedContainer,
        int? keyVersion,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset storedAtUtc,
        DateTimeOffset? availableAtUtc,
        DateTimeOffset? consumedAtUtc,
        DateTimeOffset? expiredAtUtc,
        DateTimeOffset? destroyedAtUtc,
        WorkloadIdentity? claimOwner,
        LeaseEpoch claimEpoch,
        DateTimeOffset? claimExpiresAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        RowVersion rowVersion,
        Sha256Hash handleHash)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Wave = wave;
        Generation = generation;
        State = state;
        Fingerprint = fingerprint;
        SecretStoreReference = secretStoreReference;
        AuthorizedHost = authorizedHost;
        AuthorizedContainer = authorizedContainer;
        KeyVersion = keyVersion;
        ExpiresAtUtc = expiresAtUtc;
        StoredAtUtc = storedAtUtc;
        AvailableAtUtc = availableAtUtc;
        ConsumedAtUtc = consumedAtUtc;
        ExpiredAtUtc = expiredAtUtc;
        DestroyedAtUtc = destroyedAtUtc;
        ClaimOwner = claimOwner;
        ClaimEpoch = claimEpoch;
        ClaimExpiresAtUtc = claimExpiresAtUtc;
        Correlation = correlation;
        RecordedAtUtc = recordedAtUtc;
        RowVersion = rowVersion;
        HandleHash = handleHash;
    }

    /// <summary>Registra um novo intake (geração) em estado <see cref="SasHandleState.Stored"/>, sem claim ativo.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generation"/> não é positivo.</exception>
    public static PurviewSasUploadHandle Intake(
        SasHandleId id,
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int generation,
        Sha256Hash fingerprint,
        SecretStoreHandleReference secretStoreReference,
        string authorizedHost,
        string authorizedContainer,
        int? keyVersion,
        DateTimeOffset expiresAtUtc,
        CorrelationId correlation,
        DateTimeOffset nowUtc)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), generation, "A geração do handle deve ser positiva.");
        }

        var sanitizedHost = TextValue.Require(authorizedHost, nameof(authorizedHost), 300);
        var sanitizedContainer = TextValue.Require(authorizedContainer, nameof(authorizedContainer), 100);
        var canonicalExpiresAtUtc = TruncateToMilliseconds(expiresAtUtc);
        var canonicalNowUtc = TruncateToMilliseconds(nowUtc);

        var hash = ComputeHandleHash(
            id, tenant, project, wave, generation, SasHandleState.Stored, fingerprint, secretStoreReference,
            sanitizedHost, sanitizedContainer, keyVersion, canonicalExpiresAtUtc, canonicalNowUtc, null, null, null,
            null, null, LeaseEpoch.Initial, null, correlation, canonicalNowUtc);

        return new PurviewSasUploadHandle(
            id, tenant, project, wave, generation, SasHandleState.Stored, fingerprint, secretStoreReference,
            sanitizedHost, sanitizedContainer, keyVersion, canonicalExpiresAtUtc, canonicalNowUtc, null, null, null,
            null, null, LeaseEpoch.Initial, null, correlation, canonicalNowUtc, RowVersion.None, hash);
    }

    /// <summary>
    /// Reconstrói um handle já persistido, revalidando <see cref="HandleHash"/> contra os campos REALMENTE
    /// carregados (fail-closed). <paramref name="rowVersion"/> NÃO participa do hash (token de concorrência
    /// transiente do SQL Server, não conteúdo de negócio).
    /// </summary>
    /// <exception cref="PurviewSasHandleIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static PurviewSasUploadHandle Rehydrate(
        SasHandleId id,
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        int generation,
        SasHandleState state,
        Sha256Hash fingerprint,
        SecretStoreHandleReference secretStoreReference,
        string authorizedHost,
        string authorizedContainer,
        int? keyVersion,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset storedAtUtc,
        DateTimeOffset? availableAtUtc,
        DateTimeOffset? consumedAtUtc,
        DateTimeOffset? expiredAtUtc,
        DateTimeOffset? destroyedAtUtc,
        WorkloadIdentity? claimOwner,
        LeaseEpoch claimEpoch,
        DateTimeOffset? claimExpiresAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        RowVersion rowVersion,
        Sha256Hash persistedHandleHash)
    {
        var recomputed = ComputeHandleHash(
            id, tenant, project, wave, generation, state, fingerprint, secretStoreReference, authorizedHost,
            authorizedContainer, keyVersion, expiresAtUtc, storedAtUtc, availableAtUtc, consumedAtUtc, expiredAtUtc,
            destroyedAtUtc, claimOwner, claimEpoch, claimExpiresAtUtc, correlation, recordedAtUtc);
        if (!string.Equals(recomputed.Value, persistedHandleHash.Value, StringComparison.Ordinal))
        {
            throw new PurviewSasHandleIntegrityViolationException(
                $"O handle_hash persistido para {id.Value} não corresponde ao hash recomputado a partir dos " +
                "campos carregados — handle possivelmente adulterado ou corrompido.");
        }

        return new PurviewSasUploadHandle(
            id, tenant, project, wave, generation, state, fingerprint, secretStoreReference, authorizedHost,
            authorizedContainer, keyVersion, expiresAtUtc, storedAtUtc, availableAtUtc, consumedAtUtc, expiredAtUtc,
            destroyedAtUtc, claimOwner, claimEpoch, claimExpiresAtUtc, correlation, recordedAtUtc, rowVersion,
            persistedHandleHash);
    }

    /// <summary>Transição <c>Stored -&gt; Available</c> — confirma que o segredo está pronto para UMA aquisição.</summary>
    /// <exception cref="PurviewSasLifecycleException">O handle não está em <see cref="SasHandleState.Stored"/>.</exception>
    public PurviewSasUploadHandle MarkAvailable(DateTimeOffset nowUtc)
    {
        if (State != SasHandleState.Stored)
        {
            throw new PurviewSasLifecycleException(
                $"Transição para Available inválida a partir de {State} (handle {Id.Value}).");
        }

        return Rebuild(SasHandleState.Available, nowUtc, availableAtUtc: TruncateToMilliseconds(nowUtc));
    }

    /// <summary>
    /// Transição <c>Available -&gt; Claimed</c> — reivindica exclusividade de uso único sob lease/fencing
    /// (AB-I5-006 item 2). Incrementa <see cref="ClaimEpoch"/> (mesmo padrão de fencing de <c>Job</c>): um
    /// owner que perder a corrida do <see cref="ConcurrencyException"/> subjacente (a Application persiste
    /// via <c>row_version</c>) nunca chega a observar o segredo, pois a finalização (<see cref="FinalizeClaim"/>)
    /// só é aceita sob a época devolvida por ESTA chamada.
    /// </summary>
    /// <exception cref="PurviewSasLifecycleException">O handle não está em <see cref="SasHandleState.Available"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="leaseExpiresAtUtc"/> não é estritamente futuro.</exception>
    public PurviewSasUploadHandle Claim(WorkloadIdentity owner, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset nowUtc)
    {
        if (State != SasHandleState.Available)
        {
            throw new PurviewSasLifecycleException(
                $"Transição para Claimed inválida a partir de {State} (handle {Id.Value}).");
        }

        if (leaseExpiresAtUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc), leaseExpiresAtUtc, "O lease de claim deve expirar estritamente no futuro.");
        }

        return Rebuild(
            SasHandleState.Claimed, nowUtc,
            claimOwner: owner, claimEpoch: ClaimEpoch.Next(), claimExpiresAtUtc: TruncateToMilliseconds(leaseExpiresAtUtc));
    }

    /// <summary>
    /// Reivindicação de recuperação (<c>Claimed -&gt; Claimed</c>) — SOMENTE quando o lease titular já
    /// expirou (AB-I5-006 item 2: "reclaim atômico após expiração"). Rotaciona owner/época: o titular
    /// anterior (época defasada) nunca mais consegue <see cref="FinalizeClaim"/> nem reivindicar de volta
    /// sem um novo <see cref="Claim"/>/<see cref="Reclaim"/> legítimo. Torna cancelamento/crash do
    /// adquirente anterior RECUPERÁVEL sem queimar a geração (nunca readquirível permanentemente).
    /// </summary>
    /// <exception cref="PurviewSasLifecycleException">
    /// O handle não está em <see cref="SasHandleState.Claimed"/>, ou o lease titular ainda não expirou.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="leaseExpiresAtUtc"/> não é estritamente futuro.</exception>
    public PurviewSasUploadHandle Reclaim(WorkloadIdentity newOwner, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset nowUtc)
    {
        if (State != SasHandleState.Claimed)
        {
            throw new PurviewSasLifecycleException(
                $"Reclaim inválido a partir de {State} (handle {Id.Value}) — só se aplica a um claim ativo.");
        }

        if (ClaimExpiresAtUtc is null || ClaimExpiresAtUtc.Value > nowUtc)
        {
            throw new PurviewSasLifecycleException(
                $"Reclaim recusado (fail-closed): o lease titular do handle {Id.Value} ainda não expirou.");
        }

        if (leaseExpiresAtUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc), leaseExpiresAtUtc, "O lease de claim deve expirar estritamente no futuro.");
        }

        return Rebuild(
            SasHandleState.Claimed, nowUtc,
            claimOwner: newOwner, claimEpoch: ClaimEpoch.Next(), claimExpiresAtUtc: TruncateToMilliseconds(leaseExpiresAtUtc));
    }

    /// <summary>
    /// Transição <c>Claimed -&gt; Consumed</c> — finaliza o uso único SOMENTE sob a MESMA identidade e
    /// época do claim titular (fencing, AB-I5-006 item 2) E somente quando o lease de claim e o próprio SAS
    /// ainda estão estritamente dentro da validade NO INSTANTE da finalização (<paramref name="nowUtc"/>,
    /// AB-I5-008 item 2) — nunca aceita <c>nowUtc == ClaimExpiresAtUtc</c>/<c>ExpiresAtUtc</c> como válido
    /// (boundary fail-closed). Deve ser chamada pela Application SOMENTE depois de
    /// <c>ISecretStore.AcquireAsync</c> já ter devolvido o segredo com sucesso — nunca antes (item 2:
    /// "finalize/consume somente após leitura bem-sucedida do secret store") — e com um <paramref name="nowUtc"/>
    /// relido do relógio IMEDIATAMENTE antes desta chamada, nunca um instante capturado antes da leitura do
    /// secret store (AB-I5-008 item 1/3): uma leitura lenta o suficiente para ultrapassar o lease/SAS deve
    /// falhar fechado aqui mesmo sem nenhum reclaim concorrente ter ocorrido.
    /// </summary>
    /// <exception cref="PurviewSasLifecycleException">
    /// O handle não está em <see cref="SasHandleState.Claimed"/> sob exatamente este owner/época (inclui o
    /// caso de um owner reassumido por <see cref="Reclaim"/> tentando finalizar com a época antiga), ou o
    /// lease de claim/o próprio SAS já expiraram no instante <paramref name="nowUtc"/> informado.
    /// </exception>
    public PurviewSasUploadHandle FinalizeClaim(WorkloadIdentity owner, LeaseEpoch epoch, DateTimeOffset nowUtc)
    {
        if (State != SasHandleState.Claimed || ClaimOwner != owner || ClaimEpoch != epoch)
        {
            throw new PurviewSasLifecycleException(
                $"Finalize recusado por fencing: o handle {Id.Value} não está sob o claim informado (owner + época).");
        }

        var canonicalNowUtc = TruncateToMilliseconds(nowUtc);

        if (ClaimExpiresAtUtc is not { } claimExpiresAtUtc || claimExpiresAtUtc <= canonicalNowUtc)
        {
            throw new PurviewSasLifecycleException(
                $"Finalize recusado (fail-closed): o lease de claim do handle {Id.Value} já expirou no instante da finalização.");
        }

        if (ExpiresAtUtc <= canonicalNowUtc)
        {
            throw new PurviewSasLifecycleException(
                $"Finalize recusado (fail-closed): o SAS do handle {Id.Value} já expirou no instante da finalização.");
        }

        return Rebuild(SasHandleState.Consumed, nowUtc, consumedAtUtc: canonicalNowUtc);
    }

    /// <summary>Transição para <see cref="SasHandleState.Expired"/> — nunca a partir de um estado já terminal.</summary>
    /// <exception cref="PurviewSasLifecycleException">O handle já está <see cref="SasHandleState.Expired"/> ou <see cref="SasHandleState.Destroyed"/>.</exception>
    public PurviewSasUploadHandle MarkExpired(DateTimeOffset nowUtc)
    {
        if (State is SasHandleState.Expired or SasHandleState.Destroyed)
        {
            throw new PurviewSasLifecycleException(
                $"Transição para Expired inválida a partir de {State} (handle {Id.Value}).");
        }

        return Rebuild(SasHandleState.Expired, nowUtc, expiredAtUtc: TruncateToMilliseconds(nowUtc));
    }

    /// <summary>
    /// Destrói o material local (item 12) — IDEMPOTENTE: reaplicar sobre um handle já
    /// <see cref="SasHandleState.Destroyed"/> devolve a MESMA instância sem erro.
    /// </summary>
    public PurviewSasUploadHandle Destroy(DateTimeOffset nowUtc)
    {
        if (State == SasHandleState.Destroyed)
        {
            return this;
        }

        return Rebuild(SasHandleState.Destroyed, nowUtc, destroyedAtUtc: TruncateToMilliseconds(nowUtc));
    }

    private PurviewSasUploadHandle Rebuild(
        SasHandleState newState,
        DateTimeOffset nowUtc,
        DateTimeOffset? availableAtUtc = null,
        DateTimeOffset? consumedAtUtc = null,
        DateTimeOffset? expiredAtUtc = null,
        DateTimeOffset? destroyedAtUtc = null,
        WorkloadIdentity? claimOwner = null,
        LeaseEpoch? claimEpoch = null,
        DateTimeOffset? claimExpiresAtUtc = null)
    {
        var canonicalNowUtc = TruncateToMilliseconds(nowUtc);
        var newAvailableAtUtc = availableAtUtc ?? AvailableAtUtc;
        var newConsumedAtUtc = consumedAtUtc ?? ConsumedAtUtc;
        var newExpiredAtUtc = expiredAtUtc ?? ExpiredAtUtc;
        var newDestroyedAtUtc = destroyedAtUtc ?? DestroyedAtUtc;
        var newClaimOwner = claimOwner ?? ClaimOwner;
        var newClaimEpoch = claimEpoch ?? ClaimEpoch;
        var newClaimExpiresAtUtc = claimExpiresAtUtc ?? ClaimExpiresAtUtc;

        var hash = ComputeHandleHash(
            Id, Tenant, Project, Wave, Generation, newState, Fingerprint, SecretStoreReference, AuthorizedHost,
            AuthorizedContainer, KeyVersion, ExpiresAtUtc, StoredAtUtc, newAvailableAtUtc, newConsumedAtUtc,
            newExpiredAtUtc, newDestroyedAtUtc, newClaimOwner, newClaimEpoch, newClaimExpiresAtUtc, Correlation,
            canonicalNowUtc);

        return new PurviewSasUploadHandle(
            Id, Tenant, Project, Wave, Generation, newState, Fingerprint, SecretStoreReference, AuthorizedHost,
            AuthorizedContainer, KeyVersion, ExpiresAtUtc, StoredAtUtc, newAvailableAtUtc, newConsumedAtUtc,
            newExpiredAtUtc, newDestroyedAtUtc, newClaimOwner, newClaimEpoch, newClaimExpiresAtUtc, Correlation,
            canonicalNowUtc, RowVersion, hash);
    }

    /// <summary>Identidade do handle.</summary>
    public SasHandleId Id { get; }

    /// <summary>Tenant do escopo.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda a que este handle pertence.</summary>
    public WaveId Wave { get; }

    /// <summary>Geração monotônica por (tenant, projeto, wave) — um replace cria uma nova geração (item 15).</summary>
    public int Generation { get; }

    /// <summary>Estado atual do ciclo de vida.</summary>
    public SasHandleState State { get; }

    /// <summary>Fingerprint não reversível (SHA-256) do segredo completo — nunca permite reconstruir o valor.</summary>
    public Sha256Hash Fingerprint { get; }

    /// <summary>Referência opaca ao material protegido dentro do secret store.</summary>
    public SecretStoreHandleReference SecretStoreReference { get; }

    /// <summary>Host de destino canonicalizado (metadado NÃO secreto).</summary>
    public string AuthorizedHost { get; }

    /// <summary>Container de destino canonicalizado (metadado NÃO secreto, sempre <c>ingestiondata</c>).</summary>
    public string AuthorizedContainer { get; }

    /// <summary>Versão da chave de proteção, quando o mecanismo do secret store expõe esse conceito (DPAPI baseline: <see langword="null"/>).</summary>
    public int? KeyVersion { get; }

    /// <summary>Expiry do SAS (parâmetro <c>se</c> validado) — nunca readquirível após este instante.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Instante em que este handle foi criado (intake).</summary>
    public DateTimeOffset StoredAtUtc { get; }

    /// <summary>Instante da transição para Available; <see langword="null"/> se ainda não ocorreu.</summary>
    public DateTimeOffset? AvailableAtUtc { get; }

    /// <summary>Instante da transição para Consumed; <see langword="null"/> se ainda não ocorreu.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; }

    /// <summary>Instante da transição para Expired; <see langword="null"/> se ainda não ocorreu.</summary>
    public DateTimeOffset? ExpiredAtUtc { get; }

    /// <summary>Instante da destruição local; <see langword="null"/> se ainda não ocorreu.</summary>
    public DateTimeOffset? DestroyedAtUtc { get; }

    /// <summary>
    /// Identidade titular do claim ativo/mais recente; <see langword="null"/> enquanto nenhum claim foi
    /// reivindicado (mesmo após <see cref="Reclaim"/>, retém o owner MAIS RECENTE — nunca o histórico
    /// completo, que vive na trilha de auditoria/correlação, não neste agregado).
    /// </summary>
    public WorkloadIdentity? ClaimOwner { get; }

    /// <summary>
    /// Época de fencing do claim (AB-I5-006 item 2, mesmo padrão de <see cref="LeaseEpoch"/> em <c>Job</c>):
    /// <see cref="LeaseEpoch.Initial"/> (zero) enquanto nunca reivindicado; incrementa a cada
    /// <see cref="Claim"/>/<see cref="Reclaim"/>. <see cref="FinalizeClaim"/> só aceita a época EXATA.
    /// </summary>
    public LeaseEpoch ClaimEpoch { get; }

    /// <summary>
    /// Instante em que o claim ativo/mais recente deixa (ou deixou) de ser titular — um <see cref="Reclaim"/>
    /// só é aceito quando este instante já ficou no passado. <see langword="null"/> enquanto nunca reivindicado.
    /// </summary>
    public DateTimeOffset? ClaimExpiresAtUtc { get; }

    /// <summary>Correlação com a trilha de auditoria (item 8: linkage de auditoria).</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante da última mutação persistida deste handle.</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>Token de concorrência otimista (transiente — não participa de <see cref="HandleHash"/>).</summary>
    public RowVersion RowVersion { get; }

    /// <summary>Hash determinístico de todos os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash HandleHash { get; }

    private static Sha256Hash ComputeHandleHash(
        SasHandleId id, TenantId tenant, ProjectId project, WaveId wave, int generation, SasHandleState state,
        Sha256Hash fingerprint, SecretStoreHandleReference secretStoreReference, string authorizedHost,
        string authorizedContainer, int? keyVersion, DateTimeOffset expiresAtUtc, DateTimeOffset storedAtUtc,
        DateTimeOffset? availableAtUtc, DateTimeOffset? consumedAtUtc, DateTimeOffset? expiredAtUtc,
        DateTimeOffset? destroyedAtUtc, WorkloadIdentity? claimOwner, LeaseEpoch claimEpoch,
        DateTimeOffset? claimExpiresAtUtc, CorrelationId correlation, DateTimeOffset recordedAtUtc) =>
        DeterministicHash.Compute(
        [
            id.Value.ToString("N"),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            generation.ToString(CultureInfo.InvariantCulture),
            state.ToString(),
            fingerprint.Value,
            secretStoreReference.Value,
            authorizedHost,
            authorizedContainer,
            keyVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            TruncateToMilliseconds(expiresAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            TruncateToMilliseconds(storedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            availableAtUtc is { } a ? TruncateToMilliseconds(a).UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty,
            consumedAtUtc is { } c ? TruncateToMilliseconds(c).UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty,
            expiredAtUtc is { } e ? TruncateToMilliseconds(e).UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty,
            destroyedAtUtc is { } d ? TruncateToMilliseconds(d).UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty,
            claimOwner?.Value ?? string.Empty,
            claimEpoch.Value.ToString(CultureInfo.InvariantCulture),
            claimExpiresAtUtc is { } ce ? TruncateToMilliseconds(ce).UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(recordedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
