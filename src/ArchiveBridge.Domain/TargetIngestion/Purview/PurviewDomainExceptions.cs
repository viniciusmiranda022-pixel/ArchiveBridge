namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Um <see cref="CapabilityEvidence"/> persistido não corresponde à sua própria evidência hashada — a
/// persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <c>EvWatermark</c>/<c>InventorySnapshot</c>).
/// Nunca lançada por <see cref="CapabilityEvidence.Record"/>, apenas por <see cref="CapabilityEvidence.Rehydrate"/>.
/// </summary>
public sealed class CapabilityEvidenceIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção com mensagem.</summary>
    public CapabilityEvidenceIntegrityViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Um <see cref="MailboxPrecheckSnapshot"/> persistido não corresponde à sua própria evidência hashada —
/// mesma fronteira NÃO CONFIÁVEL de <see cref="CapabilityEvidenceIntegrityViolationException"/>. Nunca
/// lançada por <see cref="MailboxPrecheckSnapshot.Observe"/>, apenas por <see cref="MailboxPrecheckSnapshot.Rehydrate"/>.
/// </summary>
public sealed class MailboxPrecheckIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção com mensagem.</summary>
    public MailboxPrecheckIntegrityViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>Validação de domínio recusada fail-closed (ex.: identidade de mailbox não resolvida).</summary>
public sealed class PurviewValidationException : Exception
{
    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewValidationException(string message)
        : base(message)
    {
    }
}
