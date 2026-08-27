namespace ArchiveBridge.Domain.Security;

/// <summary>Lançada quando um <see cref="BuildProvenanceRecord"/> JÁ PERSISTIDO falha a revalidação de integridade.</summary>
public sealed class SupplyChainIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public SupplyChainIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public SupplyChainIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public SupplyChainIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Lançada quando um <see cref="BuildProvenanceRecord"/> violaria um invariante estrutural (ex.: SHA de commit em formato inválido).</summary>
public sealed class SupplyChainProvenanceInvariantViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public SupplyChainProvenanceInvariantViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public SupplyChainProvenanceInvariantViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public SupplyChainProvenanceInvariantViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada por <see cref="ArtifactPromotionVerifier.VerifyPromotion"/> quando o digest do artifact candidato
/// diverge do digest da build APROVADA — drift entre "o que foi aprovado" e "o que está sendo promovido"
/// falha SEMPRE fechado (nunca silenciosamente aceito), mesmo quando o SHA de commit/builder alegados
/// coincidem (AB-I7-008 item 3).
/// </summary>
public sealed class SupplyChainPromotionDriftException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public SupplyChainPromotionDriftException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public SupplyChainPromotionDriftException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public SupplyChainPromotionDriftException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
