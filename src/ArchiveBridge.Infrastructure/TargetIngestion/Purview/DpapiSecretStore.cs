using System.Data;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview;

/// <summary>
/// Adapter de custódia de segredos on-premises single-node, baseline do ADR-0008 ("perfil inicial
/// autorizado: nó único; DPAPI sob identidade dedicada"; "perfil HA de segredos: BLOCKED_PENDING_EVIDENCE").
/// Protege o valor com <see cref="ProtectedData.Protect"/> sob <see cref="DataProtectionScope.CurrentUser"/>
/// — vinculado à identidade Windows dedicada do processo que executa a proteção (ADR-0008: "protegido pela
/// identidade dedicada do workload"), NUNCA <c>LocalMachine</c> (que qualquer processo/usuário do mesmo
/// host poderia desproteger). O ciphertext + entropia ficam em <c>dbo.purview_sas_secret_material</c> — o
/// SQL Server é usado apenas como armazenamento durável para bytes JÁ protegidos por DPAPI; nenhum SAS
/// texto claro/ciphertext reversível sem a identidade Windows correta chega ao SQL.
/// <para>
/// Fail-closed quando DPAPI não está disponível no ambiente atual (ex.: host não-Windows, como o runner
/// de CI deste repositório — <see cref="OperatingSystem.IsWindows"/> == <see langword="false"/>): NUNCA
/// um fallback silencioso para texto claro ou mecanismo alternativo não certificado —
/// <see cref="SecretStoreUnavailableException"/> é lançada imediatamente (work order AB-I5-004 item 6/7,
/// teste obrigatório "falha segura quando proteção não está disponível").
/// </para>
/// </summary>
/// <remarks>
/// Marcado <see cref="SupportedOSPlatformAttribute"/>("windows") para o analisador de compatibilidade de
/// plataforma (CA1416) — a instanciação real deste tipo é responsabilidade do composition root de um
/// Passo futuro, que deve guardar a instanciação com <see cref="OperatingSystem.IsWindows"/> (o mesmo
/// guard que <see cref="RequireDpapiAvailable"/> aplica em tempo de execução a CADA chamada, para o caso
/// de o host ser reconfigurado/movido para um SO diferente depois do startup).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(TenantConnectionFactory connectionFactory, IClock clock) : ISecretStore
{
    private const int EntropySizeBytes = 32;
    private const byte CurrentUserScopeCode = 0;

    private const string InsertSql =
        """
        INSERT INTO dbo.purview_sas_secret_material
            (reference_id, tenant_id, project_id, protected_bytes, entropy_bytes, protection_scope, created_at_utc)
        VALUES (@reference, @tenant, @project, @protectedBytes, @entropy, @scope, @createdAt);
        """;

    private const string SelectSql =
        """
        SELECT protected_bytes, entropy_bytes
        FROM dbo.purview_sas_secret_material
        WHERE reference_id = @reference AND project_id = @project;
        """;

    private const string DeleteSql =
        """
        DELETE FROM dbo.purview_sas_secret_material
        WHERE reference_id = @reference AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<SecretStoreHandleReference> ProtectAsync(
        TenantScope scope, RedactedSecret secret, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        RequireDpapiAvailable();

        var plaintextBytes = Encoding.UTF8.GetBytes(secret.Reveal());
        var entropy = RandomNumberGenerator.GetBytes(EntropySizeBytes);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plaintextBytes, entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            // Higiene de memória local best-effort (ADR-0008 §34: "limpar temporários") — não elimina a
            // cópia do GC/JIT, mas remove a referência mais óbvia assim que o ciphertext existe.
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        var referenceId = Guid.NewGuid();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@reference", SqlDbType.UniqueIdentifier) { Value = referenceId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@protectedBytes", SqlDbType.VarBinary, 4000) { Value = protectedBytes });
        command.Parameters.Add(new SqlParameter("@entropy", SqlDbType.VarBinary, 64) { Value = entropy });
        command.Parameters.Add(new SqlParameter("@scope", SqlDbType.TinyInt) { Value = CurrentUserScopeCode });
        command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(_clock.UtcNow) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new SecretStoreHandleReference(referenceId.ToString("N"));
    }

    /// <inheritdoc />
    public async Task<RedactedSecret> AcquireAsync(
        TenantScope scope,
        SecretStoreHandleReference reference,
        WorkloadIdentity requester,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        RequireDpapiAvailable();

        // Defesa em profundidade: revalida a identidade autorizada aqui TAMBÉM, independentemente da
        // Application (AcquireSasForUploadUseCase já recusa antes de chamar este adapter) — um chamador
        // futuro que invoque ISecretStore diretamente, fora do caso de uso, não contorna o boundary.
        if (!string.Equals(requester.Value, WorkloadIdentities.UploadWorker.Value, StringComparison.Ordinal))
        {
            throw new SecretStoreAccessDeniedException(AccessDeniedMessage);
        }

        if (!TryParseReference(reference, out var referenceId))
        {
            throw new SecretStoreAccessDeniedException(AccessDeniedMessage);
        }

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@reference", SqlDbType.UniqueIdentifier) { Value = referenceId });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        byte[] protectedBytes;
        byte[] entropy;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new SecretStoreAccessDeniedException(AccessDeniedMessage);
            }

            protectedBytes = (byte[])reader[0];
            entropy = (byte[])reader[1];
        }

        byte[] plaintextBytes;
        try
        {
            plaintextBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Não consegue desproteger sob a identidade atual (chave DPAPI diferente/corrompida) — nunca
            // tratado como "provavelmente ok"; fail-closed sem vazar detalhe do erro criptográfico.
            throw new SecretStoreAccessDeniedException(AccessDeniedMessage);
        }

        try
        {
            return RedactedSecret.Wrap(Encoding.UTF8.GetString(plaintextBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    /// <inheritdoc />
    public async Task DestroyAsync(
        TenantScope scope, SecretStoreHandleReference reference, CorrelationId correlation, CancellationToken cancellationToken)
    {
        if (!TryParseReference(reference, out var referenceId))
        {
            // Referência opaca não reconhecida: destruir algo que nunca existiu é um no-op (item 12:
            // destruição é idempotente) — nunca um erro.
            return;
        }

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(DeleteSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@reference", SqlDbType.UniqueIdentifier) { Value = referenceId });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); // 0 linhas afetadas = já destruído, idempotente.
    }

    private const string AccessDeniedMessage =
        "Aquisição recusada (fail-closed): identidade não autorizada ou referência inexistente/fora do escopo.";

    private static void RequireDpapiAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SecretStoreUnavailableException(
                "DPAPI não está disponível neste sistema operacional; custódia recusada fail-closed (ADR-0008 " +
                "— perfil de nó único DPAPI; nenhum fallback inseguro é aceito).");
        }
    }

    private static bool TryParseReference(SecretStoreHandleReference reference, out Guid referenceId) =>
        Guid.TryParseExact(reference.Value, "N", out referenceId);
}
