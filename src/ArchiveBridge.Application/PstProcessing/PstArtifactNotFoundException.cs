namespace ArchiveBridge.Application.PstProcessing;

/// <summary>
/// Lançada quando o artefato solicitado não existe OU não pertence ao escopo tenant/projeto do chamador.
/// Deliberadamente indistinguível entre os dois casos (anti-IDOR — nunca revela existência de um artefato
/// de outro tenant/projeto).
/// </summary>
public sealed class PstArtifactNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PstArtifactNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PstArtifactNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PstArtifactNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
