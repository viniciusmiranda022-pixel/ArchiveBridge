namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Lançada pelo parser (AB-I6-001 item 6) quando o validation report / service result não pode ser
/// interpretado de forma estrita e bounded: tamanho excedido, linhas em excesso, encoding inválido,
/// cabeçalho ausente/desconhecido, campo em excesso/faltando numa linha, identidade de PST duplicada, ou
/// qualquer outro desvio do formato exigido. Nunca produz um relatório parcial — o relatório inteiro é
/// recusado (fail-closed).
/// </summary>
public sealed class PurviewServiceResultParsingException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewServiceResultParsingException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewServiceResultParsingException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewServiceResultParsingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando uma ou mais linhas do relatório não correlacionam 1:1 com a cadeia canônica
/// <c>WaveEntry ↔ Binding ↔ PartitionExecution ↔ Upload manifest ↔ Mapping</c> da onda (AB-I6-001 item 8):
/// nome remoto desconhecido, duplicado dentro do relatório, ou o relatório afirma completude (contagem
/// total declarada) e a contagem real diverge. Nunca descarta silenciosamente a linha ambígua — o
/// relatório inteiro é recusado (fail-closed/inconclusivo).
/// </summary>
public sealed class PurviewServiceResultCorrelationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewServiceResultCorrelationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewServiceResultCorrelationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewServiceResultCorrelationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando uma evidência de service result JÁ PERSISTIDA falha a revalidação de integridade (hash
/// do conteúdo bruto ou das linhas normalizadas recomputado diverge do persistido) — a persistência é
/// fronteira NÃO CONFIÁVEL: uma linha/artefato corrompido ou adulterado nunca é reidratado, devolvido ou
/// reaproveitado como evidência válida.
/// </summary>
public sealed class PurviewServiceResultIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewServiceResultIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewServiceResultIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewServiceResultIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
