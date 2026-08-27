namespace ArchiveBridge.Domain.Recovery;

/// <summary>
/// Lançada quando um <see cref="RecoveryReadinessRecord"/> JÁ PERSISTIDO falha a revalidação de
/// integridade (<see cref="RecoveryReadinessRecord.RecordHash"/> recomputado diverge do persistido) — a
/// persistência é fronteira NÃO CONFIÁVEL: um registro adulterado/corrompido nunca é reidratado,
/// devolvido, verificado como válido ou autorreparado silenciosamente (AB-I7-005 item 7/invariantes).
/// </summary>
public sealed class RecoveryReadinessIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public RecoveryReadinessIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public RecoveryReadinessIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public RecoveryReadinessIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta construir um <see cref="RecoveryReadinessRecord"/> com
/// <see cref="RecoveryReadinessStatus.Pass"/> sem uma medição real, ou cuja medição real excede o alvo
/// objetivo documentado — nunca é possível declarar sucesso de RTO/RPO por configuração/alegação (AB-I7-005
/// item 2/9/invariantes: "Unknown/NotMeasured nunca vira Ready/Pass").
/// </summary>
public sealed class RecoveryReadinessObjectiveNotMetException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public RecoveryReadinessObjectiveNotMetException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public RecoveryReadinessObjectiveNotMetException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public RecoveryReadinessObjectiveNotMetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
