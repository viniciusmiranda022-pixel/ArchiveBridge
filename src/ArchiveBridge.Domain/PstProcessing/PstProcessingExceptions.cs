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

/// <summary>
/// Lançada pela store de planos quando uma execução concorrente já persistiu o plano CANÔNICO para a mesma
/// identidade determinística (mesmo <c>planHash</c>) — o índice único filtrado no SQL Server venceu a
/// corrida antes desta chamada. Fail-closed: nenhuma linha duplicada é criada; o chamador deve reler o
/// plano canônico existente (réplay), nunca tratar isto como falha de negócio.
/// </summary>
public sealed class PartitionPlanConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionPlanConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionPlanConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionPlanConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando um <see cref="PartitionPlanConflictException"/> foi capturado mas a
/// releitura subsequente NÃO encontrou o plano canônico — estado impossível sob o invariante de
/// canonicidade (quem venceu a corrida DEVERIA estar persistido). Fail-closed: o chamador nunca recebe de
/// volta um <see cref="PartitionPlan"/> que não foi persistido.
/// </summary>
public sealed class PartitionPlanConflictUnresolvedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionPlanConflictUnresolvedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionPlanConflictUnresolvedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionPlanConflictUnresolvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada por <see cref="PartitionPlan.Rehydrate"/> quando uma linha JÁ PERSISTIDA viola um invariante
/// estrutural do agregado (partes não contíguas, soma divergente do tamanho de origem, parte acima do limite
/// duro, <c>covers_entire_source</c> incoerente, campos obrigatórios ausentes) ou quando a identidade
/// determinística gravada não corresponde às próprias entradas persistidas (<c>plan_hash</c>/<c>part_key</c>
/// recalculados divergem). A persistência é uma fronteira NÃO CONFIÁVEL: uma linha corrompida ou adulterada
/// nunca pode ser reidratada, devolvida ou reaproveitada em réplay como se fosse um plano válido — e nunca é
/// silenciosamente normalizada. Fail-closed.
/// </summary>
public sealed class PartitionPlanIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionPlanIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionPlanIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionPlanIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando a store de planos devolve, como canônico, um <see cref="PartitionPlan"/>
/// que o Domain não reconhece como tal (<see cref="PartitionPlan.IsCanonical"/> falso) ou cuja identidade
/// determinística não é a esperada. Defesa em profundidade: a Application nunca reaproveita cegamente um
/// plano só porque veio do método de leitura canônica.
/// </summary>
public sealed class PartitionPlanCanonicityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionPlanCanonicityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionPlanCanonicityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionPlanCanonicityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando o plano de particionamento solicitado não existe OU não pertence ao escopo tenant/projeto
/// do chamador. Deliberadamente indistinguível entre os dois casos (anti-IDOR — nunca revela existência de
/// um plano de outro tenant/projeto).
/// </summary>
public sealed class PartitionPlanNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionPlanNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionPlanNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionPlanNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application (Slice 4B, Passo 3) quando o plano/parte solicitado NÃO é elegível para
/// execução: precisa ser exatamente <see cref="PartitionPlanOutcome.Planned"/> com
/// <see cref="PartitionPlanReason.SinglePartWithinTarget"/>, uma única parte cobrindo a origem inteira,
/// identidade determinística consistente (<see cref="PartitionPlan.HasConsistentIdentity"/>) e canônico.
/// Qualquer outro caso (Unsupported/Blocked, plano não canônico, identidade forjada, múltiplas partes)
/// falha fechado SEM criar output e SEM qualquer efeito externo — nem arquivo, nem linha de evidência.
/// </summary>
public sealed class PartitionExecutionNotEligibleException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionNotEligibleException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionNotEligibleException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionNotEligibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando o hash de custódia REGISTRADO no momento da execução diverge do
/// <see cref="PartitionPlanSource.SourceHash"/> gravado no plano — o artefato mudou desde que o plano foi
/// calculado. Fail-closed: nunca executa sobre uma origem obsoleta, sem qualquer efeito externo.
/// </summary>
public sealed class PartitionExecutionSourceStaleException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionSourceStaleException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionSourceStaleException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionSourceStaleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela store de execuções quando uma execução concorrente já persistiu o resultado canônico para a
/// mesma (tenant, projeto, plano, parte) — o índice único no SQL Server venceu a corrida antes desta
/// chamada. Fail-closed: nenhuma linha duplicada é criada; o chamador deve reler o resultado canônico
/// existente (réplay), nunca tratar isto como falha de negócio.
/// </summary>
public sealed class PartitionExecutionConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando um <see cref="PartitionExecutionConflictException"/> foi capturado mas a
/// releitura subsequente NÃO encontrou a execução canônica — estado impossível sob o invariante de
/// canonicidade (quem venceu a corrida DEVERIA estar persistido). Fail-closed: o chamador nunca recebe de
/// volta um <see cref="PartitionExecutionRecord"/> que não foi persistido.
/// </summary>
public sealed class PartitionExecutionConflictUnresolvedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionConflictUnresolvedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionConflictUnresolvedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionConflictUnresolvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada por <see cref="PartitionExecutionRecord.Rehydrate"/> quando uma linha JÁ PERSISTIDA viola um
/// invariante estrutural do agregado (identidade de parte incoerente, saída não byte-for-byte da origem,
/// timestamps invertidos). A persistência é uma fronteira NÃO CONFIÁVEL: uma linha corrompida ou adulterada
/// nunca pode ser reidratada, devolvida ou reaproveitada em réplay como se fosse uma execução válida — e
/// nunca é silenciosamente normalizada. Fail-closed.
/// </summary>
public sealed class PartitionExecutionIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando a store de execuções devolve, como canônica, uma
/// <see cref="PartitionExecutionRecord"/> cuja identidade determinística não é a esperada
/// (<see cref="PartitionExecutionRecord.HasConsistentIdentity"/> falso ou <c>PlanHash</c> divergente).
/// Defesa em profundidade: a Application nunca reaproveita cegamente uma execução só porque veio do método
/// de leitura canônica.
/// </summary>
public sealed class PartitionExecutionCanonicityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionCanonicityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionCanonicityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionCanonicityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pelo writer quando um output JÁ EXISTENTE no caminho canônico determinístico (mesmos IDs opacos)
/// NÃO confere com o hash/tamanho esperados da origem — adulteração ou corrupção detectada na
/// reabertura/reinspeção. Fail-closed: NUNCA sobrescreve automaticamente um output existente; a disposição
/// exige decisão explícita fora deste caminho de código (o mesmo output nunca é regenerado silenciosamente
/// — runbook §20.5).
/// </summary>
public sealed class PartitionExecutionOutputTamperedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PartitionExecutionOutputTamperedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PartitionExecutionOutputTamperedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PartitionExecutionOutputTamperedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pelo writer quando um limite de recurso/tempo configurado é excedido ANTES da conclusão da
/// execução (ex.: espaço em disco insuficiente no preflight, timeout de cópia). Fail-closed: nunca é
/// reportado como sucesso; <see cref="ReasonCode"/> é sempre um valor sanitizado (sem stack trace, sem
/// caminho real) — seguro para evidência/auditoria. Nenhum output canônico é publicado.
/// </summary>
public sealed class PartitionExecutionLimitExceededException : Exception
{
    /// <summary>Cria a exceção com o código de motivo sanitizado.</summary>
    public PartitionExecutionLimitExceededException(string reasonCode)
        : base($"Limite de execução de particionamento excedido: {reasonCode}.")
    {
        ReasonCode = reasonCode;
    }

    /// <summary>Cria a exceção com o código de motivo sanitizado e causa.</summary>
    public PartitionExecutionLimitExceededException(string reasonCode, Exception innerException)
        : base($"Limite de execução de particionamento excedido: {reasonCode}.", innerException)
    {
        ReasonCode = reasonCode;
    }

    /// <summary>Código curto e sanitizado do limite excedido (ex.: <c>INSUFFICIENT_SPACE</c>, <c>TIMEOUT</c>).</summary>
    public string ReasonCode { get; }
}
