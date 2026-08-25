namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Lançada quando um campo/pasta de uma observação de estatísticas EXO viola uma invariante de domínio
/// (AB-I6-005 item 9): path/tipo de pasta inválido/oversized, pasta duplicada, excesso de pastas ou data
/// temporalmente impossível (<c>OldestItemReceivedDateUtc</c> posterior a <c>NewestItemReceivedDateUtc</c>).
/// Fail-closed: a observação inteira é recusada, nunca aceita parcialmente.
/// </summary>
public sealed class ExoArchiveStatisticsValidationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ExoArchiveStatisticsValidationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ExoArchiveStatisticsValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ExoArchiveStatisticsValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando a onda ou o archive de destino referenciado não existe ou não pertence ao escopo
/// tenant/projeto do chamador — mesmo padrão anti-IDOR de <c>PurviewArchiveNotFoundException</c>/
/// <c>PurviewImportJobSourceNotFoundException</c> (nunca revela existência de uma onda/archive de outro
/// tenant/projeto, nem sonda o adapter antes desta checagem passar).
/// </summary>
public sealed class ExoArchiveStatisticsSourceNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ExoArchiveStatisticsSourceNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ExoArchiveStatisticsSourceNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ExoArchiveStatisticsSourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando <see cref="ExoStatisticsPhase.AfterImport"/> é solicitado antes de existir evidência
/// canônica suficiente de que a importação Purview observada concluiu a etapa necessária para iniciar
/// reconciliação (AB-I6-005 itens 4/critério de aceite 2). O adapter NUNCA é sondado quando esta exceção
/// é lançada.
/// </summary>
public sealed class ExoArchiveStatisticsPrerequisiteException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ExoArchiveStatisticsPrerequisiteException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ExoArchiveStatisticsPrerequisiteException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ExoArchiveStatisticsPrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando um snapshot de estatísticas EXO ou suas estatísticas de pasta filhas JÁ PERSISTIDOS
/// falham a revalidação de integridade (hash recomputado diverge do persistido, ou contagem/conteúdo das
/// pastas carregadas diverge do header) — a persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de
/// <c>PurviewServiceResultIntegrityViolationException</c>): um registro adulterado/corrompido nunca é
/// reidratado, devolvido ou reaproveitado como evidência válida.
/// </summary>
public sealed class ExoArchiveStatisticsIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ExoArchiveStatisticsIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ExoArchiveStatisticsIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ExoArchiveStatisticsIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
