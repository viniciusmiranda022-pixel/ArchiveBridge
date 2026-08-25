namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Lançada quando a onda ou o plano de import job referenciado não existe ou não pertence ao escopo
/// tenant/projeto do chamador — mesmo padrão anti-IDOR de
/// <c>TargetIngestion.Purview.MappingCsv.PurviewMappingCsvSourceNotFoundException</c> (nunca revela
/// existência de uma onda/plano de outro tenant/projeto).
/// </summary>
public sealed class PurviewImportJobSourceNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewImportJobSourceNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewImportJobSourceNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewImportJobSourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando um pré-requisito obrigatório para aceitar evidência de job/resultado do Purview não
/// está satisfeito (AB-I6-001 item 3): upload não verificado, mapping CSV não publicado/canônico, drift
/// entre o mapping publicado e o estado ATUAL de vínculos/execuções/upload, ou campo observado (horário,
/// status) fora dos limites plausíveis. Nunca produz evidência parcial.
/// </summary>
public sealed class PurviewImportJobPrerequisiteException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewImportJobPrerequisiteException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewImportJobPrerequisiteException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewImportJobPrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando um <see cref="PurviewProviderOperationId"/> observado seria associado de forma
/// incompatível com a chave lógica server-side (AB-I6-001 item 5): reassociar o MESMO plano/nome
/// planejado a um ID de provider diferente do já registrado, ou reaproveitar o MESMO ID de provider para
/// um plano/onda diferente dentro do escopo. Fail-closed: nenhuma das duas reassociações é aceita
/// silenciosamente.
/// </summary>
public sealed class PurviewImportJobIdentityConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewImportJobIdentityConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewImportJobIdentityConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewImportJobIdentityConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando um plano ou observação de import job JÁ PERSISTIDO falha a revalidação de integridade
/// (hash recomputado diverge do persistido) — a persistência é fronteira NÃO CONFIÁVEL (mesmo princípio
/// de <c>WavePartitionOutputBindingIntegrityViolationException</c>): uma linha adulterada/corrompida
/// nunca é reidratada, devolvida ou reaproveitada como evidência válida.
/// </summary>
public sealed class PurviewImportJobIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewImportJobIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewImportJobIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewImportJobIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
