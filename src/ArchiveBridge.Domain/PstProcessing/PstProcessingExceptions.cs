namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Lançada pela store quando uma tentativa concorrente já persistiu o resultado CANÔNICO para o mesmo
/// (tenant, projeto, artefato, hash esperado) — o backstop do índice único filtrado no SQL Server venceu a
/// corrida antes desta chamada. Fail-closed: nenhuma linha duplicada é criada; o chamador deve reler o
/// resultado canônico já existente (réplay), nunca tratar isto como falha de negócio.
/// </summary>
public sealed class PstInspectionConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PstInspectionConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PstInspectionConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PstInspectionConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando um <see cref="PstInspectionConflictException"/> foi capturado (o
/// backstop de índice único da store venceu uma corrida) mas a releitura subsequente de
/// <c>IPstInspectionStore.FindCanonicalAsync</c> NÃO encontrou nenhum resultado canônico — um estado que
/// não deveria ser possível sob o invariante de canonicidade (alguém venceu a corrida, então DEVERIA existir
/// um canônico). Fail-closed: o chamador nunca recebe de volta um <c>PstInspectionRecord</c> que não foi
/// persistido; esta exceção sinaliza corrupção/condição inesperada exigindo investigação, nunca um erro de
/// negócio normal.
/// </summary>
public sealed class PstInspectionConflictUnresolvedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PstInspectionConflictUnresolvedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PstInspectionConflictUnresolvedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PstInspectionConflictUnresolvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando <c>IPstInspectionStore.FindCanonicalAsync</c> devolve um
/// <see cref="PstInspectionRecord"/> cujo <see cref="PstInspectionRecord.IsCanonical"/> é <c>false</c> — a
/// store nunca deveria devolver um resultado não-canônico como se fosse o canônico reaproveitável. Defesa em
/// profundidade: a Application NUNCA confia cegamente no que a store devolve como "canônico" só porque veio
/// desse método; revalida o invariante do Domain antes de reaproveitar (réplay idempotente) o resultado.
/// Fail-closed: nunca reaproveita silenciosamente um resultado que o próprio Domain não reconhece como
/// canônico.
/// </summary>
public sealed class PstInspectionCanonicityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PstInspectionCanonicityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PstInspectionCanonicityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PstInspectionCanonicityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela engine quando um limite de tamanho/tempo/recursos configurado é excedido antes da
/// conclusão da inspeção. Fail-closed: nunca é reportado como sucesso; a mensagem e <see cref="ReasonCode"/>
/// são sempre valores sanitizados (sem stack trace, sem caminho real) — seguros para evidência/auditoria.
/// </summary>
public sealed class PstInspectionLimitExceededException : Exception
{
    /// <summary>Cria a exceção com o código de motivo sanitizado.</summary>
    public PstInspectionLimitExceededException(string reasonCode)
        : base($"Limite de inspeção excedido: {reasonCode}.")
    {
        ReasonCode = reasonCode;
    }

    /// <summary>Cria a exceção com o código de motivo sanitizado e causa.</summary>
    public PstInspectionLimitExceededException(string reasonCode, Exception innerException)
        : base($"Limite de inspeção excedido: {reasonCode}.", innerException)
    {
        ReasonCode = reasonCode;
    }

    /// <summary>Código curto e sanitizado do limite excedido (ex.: <c>MAX_SIZE_EXCEEDED</c>, <c>TIMEOUT</c>).</summary>
    public string ReasonCode { get; }
}
