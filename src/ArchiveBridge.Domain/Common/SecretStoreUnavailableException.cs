namespace ArchiveBridge.Domain.Common;

/// <summary>
/// O mecanismo de proteção de segredos on-premises (ex.: DPAPI) não está disponível no ambiente atual de
/// execução (ex.: host não-Windows). Fail-closed: NUNCA um fallback silencioso para texto claro ou para
/// um mecanismo alternativo não certificado — a operação é recusada (ADR-0008: "perfil HA de segredos
/// permanece BLOCKED_PENDING_EVIDENCE"; nenhuma pseudo-HA/fallback inseguro).
/// </summary>
public sealed class SecretStoreUnavailableException : Exception
{
    /// <summary>Cria a exceção com mensagem.</summary>
    public SecretStoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public SecretStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
