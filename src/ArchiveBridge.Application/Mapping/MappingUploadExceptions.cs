namespace ArchiveBridge.Application.Mapping;

/// <summary>
/// Motivo de uma rejeição PRÉ-CUSTÓDIA de upload (nenhuma tentativa durável é criada): o conteúdo não é
/// considerado integralmente recebido/validável. Códigos estáveis para o futuro mapeamento HTTP.
/// </summary>
public enum MappingUploadRejectionReason
{
    /// <summary>Extensão do arquivo diferente de <c>.csv</c>.</summary>
    InvalidExtension,

    /// <summary>Nome do arquivo ausente, longo demais ou com caracteres de controle.</summary>
    InvalidFileName,

    /// <summary>Comprimento declarado (preflight) acima do limite efetivo.</summary>
    DeclaredLengthTooLarge,

    /// <summary>Comprimento declarado (preflight) negativo — entrada inválida, rejeitada imediatamente.</summary>
    InvalidDeclaredLength,

    /// <summary>O conteúdo real do stream excede o limite efetivo.</summary>
    ContentTooLarge,
}

/// <summary>
/// Lançada em rejeições PRÉ-CUSTÓDIA: extensão inválida, nome inválido, comprimento declarado acima do
/// limite, ou conteúdo real acima do limite. Como o conteúdo não foi integralmente recebido de forma
/// confiável (ou sequer lido), NENHUMA tentativa de validação é persistida. Não carrega o nome bruto do
/// cliente na mensagem (higiene de logs).
/// </summary>
public sealed class MappingUploadRejectedException : Exception
{
    /// <summary>Cria a exceção com o motivo estruturado.</summary>
    public MappingUploadRejectedException(MappingUploadRejectionReason reason)
        : base($"Upload de mapping rejeitado antes da custódia ({reason}).")
    {
        Reason = reason;
    }

    /// <summary>Motivo estruturado da rejeição pré-custódia.</summary>
    public MappingUploadRejectionReason Reason { get; }
}

/// <summary>
/// Lançada quando a onda-fonte existe no escopo mas seu estado não permite validação de mapping (apenas
/// <c>Approved</c>/<c>Frozen</c> são fonte imutável aceita). É uma falha de PRECONDIÇÃO pré-custódia:
/// nenhuma tentativa é persistida.
/// </summary>
public sealed class MappingUploadPreconditionException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MappingUploadPreconditionException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MappingUploadPreconditionException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MappingUploadPreconditionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
