namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Lançada quando a entrada fornecida a uma função de correlação pura deste Passo viola um invariante
/// estrutural — ex.: linhas observadas com o MESMO nome remoto de PST (item 7: "item... duplicado... deve
/// aparecer explicitamente como exceção de reconciliação, não ser descartado" — uma duplicidade estrutural
/// na própria entrada nunca é silenciosamente resolvida escolhendo uma das duas). Fail-closed: nenhuma
/// avaliação parcial é produzida a partir de uma entrada estruturalmente inválida.
/// </summary>
public sealed class ReconciliationValidationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationValidationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando uma avaliação de reconciliação (header ou itens filhos) JÁ PERSISTIDA falha a
/// revalidação de integridade (hash recomputado diverge do persistido, ou contagem/conteúdo dos itens
/// carregados diverge do header) — a persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de
/// <c>PurviewServiceResultIntegrityViolationException</c>/<c>ExoArchiveStatisticsIntegrityViolationException</c>):
/// uma avaliação adulterada/corrompida nunca é reidratada, devolvida ou reaproveitada como evidência
/// canônica.
/// </summary>
public sealed class ReconciliationIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
