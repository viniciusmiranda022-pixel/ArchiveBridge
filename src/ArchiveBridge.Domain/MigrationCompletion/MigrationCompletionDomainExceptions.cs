namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Lançada quando UMA <see cref="MigrationCompletionAssessment"/> ou <see cref="MigrationCompletionCriterionAttestation"/>
/// JÁ PERSISTIDA falha a revalidação de integridade — a persistência é fronteira NÃO CONFIÁVEL: um registro
/// adulterado/corrompido nunca é reidratado, devolvido, verificado como válido ou autorreparado silenciosamente.
/// </summary>
public sealed class MigrationCompletionIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MigrationCompletionIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MigrationCompletionIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MigrationCompletionIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta submeter atestação de um critério por um caminho incompatível com a classificação
/// fixa do catálogo (§49): atestação de operador para um critério <c>SystemDerived</c> — bloqueio estrutural.
/// </summary>
public sealed class MigrationCompletionAttestationNotAllowedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MigrationCompletionAttestationNotAllowedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MigrationCompletionAttestationNotAllowedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MigrationCompletionAttestationNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
