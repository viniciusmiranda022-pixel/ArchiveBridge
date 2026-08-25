using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Nome/ID do Purview job observado pelo operador APÓS a criação humana no portal (runbook §25.9 item
/// 75) — evidência OBSERVADA, nunca a chave lógica interna (AB-I6-001 item 5: a chave lógica permanece
/// <see cref="PurviewImportJobName"/>, determinística e server-side). Texto livre limitado, sanitizado
/// (sem caractere de controle, sem espaço nas extremidades) e bounded — nunca confiado como identificador
/// estruturado (o portal não documenta um formato certificado para este valor).
/// </summary>
public sealed record PurviewProviderOperationId
{
    /// <summary>Tamanho máximo persistido (mesma ordem de grandeza histórica de <c>portal_job_id nvarchar(300)</c>).</summary>
    public const int MaxLength = 300;

    private PurviewProviderOperationId(string value) => Value = value;

    /// <summary>Valor observado, sanitizado.</summary>
    public string Value { get; }

    /// <summary>Cria a identidade observada a partir do texto informado pelo operador, validando forma/tamanho.</summary>
    /// <exception cref="ArgumentException">Vazio, com caractere de controle ou excede o tamanho máximo.</exception>
    public static PurviewProviderOperationId Create(string value) => new(TextValue.Require(value, nameof(value), MaxLength));

    /// <summary>
    /// Reconstrói a identidade a partir do valor JÁ PERSISTIDO (uso exclusivo da camada de persistência).
    /// </summary>
    /// <exception cref="PurviewImportJobIntegrityViolationException">Vazio, com caractere de controle ou excede o tamanho máximo.</exception>
    public static PurviewProviderOperationId FromPersistedValue(string value)
    {
        try
        {
            return new PurviewProviderOperationId(TextValue.Require(value, nameof(value), MaxLength));
        }
        catch (ArgumentException exception)
        {
            throw new PurviewImportJobIntegrityViolationException(
                "provider_operation_id persistido é inválido (fail-closed).", exception);
        }
    }
}
