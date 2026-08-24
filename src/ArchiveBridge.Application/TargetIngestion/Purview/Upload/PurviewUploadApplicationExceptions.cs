namespace ArchiveBridge.Application.TargetIngestion.Purview.Upload;

/// <summary>
/// Lançada por <see cref="RequestPurviewUploadUseCase"/> quando a onda solicitada não existe, não pertence
/// ao escopo autorizado, ou sua seleção ainda está mutável (não Approved/Frozen). Causas deliberadamente
/// indistinguíveis (anti-IDOR).
/// </summary>
public sealed class PurviewUploadWaveNotEligibleException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewUploadWaveNotEligibleException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewUploadWaveNotEligibleException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewUploadWaveNotEligibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
