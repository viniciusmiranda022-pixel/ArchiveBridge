using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;

/// <summary>
/// Requisição de UM upload de arquivo via AzCopy. A Application NUNCA resolve/conhece o caminho físico
/// local — apenas identifica a execução de partição JÁ FISICAMENTE REVALIDADA (item 12, via
/// <c>IPartitionPartVerifier</c>) por seus IDs opacos; o adapter de Infrastructure resolve o mesmo caminho
/// canônico determinístico (VersionDir) já usado pelo verificador do Slice 4B — nunca um caminho
/// independente que pudesse divergir do que foi revalidado. <see cref="DestinationSasUrl"/> é o SAS de
/// destino completo (URL do container + caminho remoto + query string, item 3) — nunca aceito de fora do
/// worker; construído SOMENTE pelo command processor a partir do handle adquirido e do
/// <see cref="ArchiveBridge.Domain.TargetIngestion.Purview.Upload.PurviewRemoteUploadPrefix"/>/<see cref="PurviewRemotePstName"/>
/// canônicos — a Application NUNCA revela/combina o valor do SAS: entrega <see cref="ContainerSas"/> (o
/// segredo, intacto) e <see cref="RemotePrefix"/>/<see cref="RemoteName"/> (metadados NÃO secretos)
/// SEPARADOS; SOMENTE o adapter de Infrastructure — a fronteira de custódia do worker dedicado (item 7) —
/// combina os dois ao montar o <c>ProcessStartInfo.ArgumentList</c>, no menor escopo de código possível. A
/// Application também não escolhe diretório de log/plan — <see cref="Attempt"/> é o único identificador
/// necessário; o adapter deriva o diretório server-side DEDICADO por tentativa (item 6:
/// <c>AZCOPY_LOG_LOCATION</c>/<c>AZCOPY_JOB_PLAN_LOCATION</c>) da sua PRÓPRIA raiz configurada.
/// </summary>
public sealed record AzCopyUploadFileRequest(
    TenantScope Scope,
    PartitionExecutionRecord Execution,
    RedactedSecret ContainerSas,
    PurviewRemoteUploadPrefix RemotePrefix,
    PurviewRemotePstName RemoteName,
    PurviewUploadAttemptId Attempt,
    TimeSpan Timeout);

/// <summary>
/// Resultado de UM upload de arquivo — SOMENTE evidência sanitizada (item 10): NUNCA stdout/stderr bruto
/// (que poderia conter o SAS/path sensível refletido pelo próprio AzCopy). Qualquer êxito/falha é
/// determinado exclusivamente por <see cref="ExitCode"/>/<see cref="TimedOut"/>/<see cref="OutputLimitExceeded"/>.
/// </summary>
public sealed record AzCopyUploadFileResult(int ExitCode, bool TimedOut, bool OutputLimitExceeded);

/// <summary>
/// Adapter isolado (Infrastructure/worker) do binário AzCopy homologado (item 6: <c>ProcessStartInfo.ArgumentList</c>,
/// nunca shell/string concatenada). <see cref="ProbeBinaryAsync"/> observa a versão/hash do binário
/// CONFIGURADO sem executar nenhum upload (usado pelo pré-check fail-closed do item 5, ANTES de sequer
/// adquirir o SAS); <see cref="UploadFileAsync"/> executa o transporte de UM arquivo já revalidado.
/// </summary>
public interface IAzCopyUploadExecutor
{
    /// <summary>
    /// Observa a versão declarada e o SHA-256 REAL do binário AzCopy configurado no worker, sem executar
    /// nenhum upload. Lança <see cref="AzCopyBinaryUnavailableException"/> se o binário não existir/não
    /// puder ser lido — fail-closed, nunca um "não sei" tratado como sucesso.
    /// </summary>
    Task<AzCopyBinaryIdentity> ProbeBinaryAsync(CancellationToken cancellationToken);

    /// <summary>Executa o transporte de UM arquivo já fisicamente revalidado para o destino SAS informado.</summary>
    Task<AzCopyUploadFileResult> UploadFileAsync(AzCopyUploadFileRequest request, CancellationToken cancellationToken);
}

/// <summary>Lançada quando o binário AzCopy configurado não existe ou não pôde ser lido/hasheado — fail-closed.</summary>
public sealed class AzCopyBinaryUnavailableException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public AzCopyBinaryUnavailableException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public AzCopyBinaryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public AzCopyBinaryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
