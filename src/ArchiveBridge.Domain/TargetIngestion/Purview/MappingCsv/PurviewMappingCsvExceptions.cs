namespace ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Lançada quando a geração do mapping CSV do Purview não pode prosseguir: onda não elegível, nenhum
/// vínculo canônico de output, execução divergente do vínculo, entrada não-membro da seleção corrente,
/// evidência de upload ausente/não verificada/divergente dos vínculos atuais, identidade de mailbox não
/// resolvida, ou qualquer outro invariante fail-closed do work order AB-I5-012. Nunca gera um CSV parcial.
/// </summary>
public sealed class PurviewMappingCsvGenerationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewMappingCsvGenerationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewMappingCsvGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewMappingCsvGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando a onda referenciada não existe ou não pertence ao escopo tenant/projeto do chamador —
/// mesmo padrão anti-IDOR usado em <c>WavePartitionOutputBindingSourceNotFoundException</c> (nunca revela
/// existência de uma onda de outro tenant/projeto).
/// </summary>
public sealed class PurviewMappingCsvSourceNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewMappingCsvSourceNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewMappingCsvSourceNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewMappingCsvSourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando uma <see cref="PurviewMappingCsvVersion"/> JÁ PERSISTIDA falha a revalidação de
/// integridade (fingerprint/hash recomputado diverge do persistido) — a persistência é fronteira NÃO
/// CONFIÁVEL (mesmo princípio de <c>WavePartitionOutputBinding</c>/<c>MailboxPrecheckSnapshot</c>): uma
/// linha corrompida ou adulterada nunca é reidratada, devolvida ou reaproveitada como evidência válida.
/// </summary>
public sealed class PurviewMappingCsvIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewMappingCsvIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewMappingCsvIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewMappingCsvIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
