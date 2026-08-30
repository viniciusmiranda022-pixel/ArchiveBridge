namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Lançada quando um <see cref="ProductionReadinessReviewSnapshot"/> ou <see cref="ReadinessControlAttestation"/>
/// JÁ PERSISTIDO falha a revalidação de integridade — a persistência é fronteira NÃO CONFIÁVEL: um registro
/// adulterado/corrompido nunca é reidratado, devolvido, verificado como válido ou autorreparado
/// silenciosamente (AB-I8-001 escopo item 7).
/// </summary>
public sealed class ProductionReadinessIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ProductionReadinessIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ProductionReadinessIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ProductionReadinessIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta atestar manualmente um controle cuja <see cref="ReadinessControlEvidenceSource"/>
/// é <see cref="ReadinessControlEvidenceSource.SystemDerived"/> (AB-I8-001: pen-test/RTO/RPO/SBOM/WDAC/
/// incident-response/hashes-manifests-lineage/backup-restore/target-root-policy/import-limits NUNCA podem
/// ser "aprovados" por alegação humana — bloqueio estrutural, não apenas convenção de chamada).
/// </summary>
public sealed class ProductionReadinessAttestationNotAllowedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ProductionReadinessAttestationNotAllowedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ProductionReadinessAttestationNotAllowedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ProductionReadinessAttestationNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
