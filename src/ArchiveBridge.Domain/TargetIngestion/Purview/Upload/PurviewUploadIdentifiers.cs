namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>Identidade do pedido lógico de upload Purview de UMA wave (AB-I5-009 item 8), gerada pelo servidor.</summary>
public readonly record struct PurviewUploadRequestId(Guid Value)
{
    /// <summary>Gera uma nova identidade de pedido.</summary>
    public static PurviewUploadRequestId New() => new(Guid.NewGuid());
}

/// <summary>Identidade de UMA tentativa de upload (append-only, item 8), gerada pelo servidor.</summary>
public readonly record struct PurviewUploadAttemptId(Guid Value)
{
    /// <summary>Gera uma nova identidade de tentativa.</summary>
    public static PurviewUploadAttemptId New() => new(Guid.NewGuid());
}
