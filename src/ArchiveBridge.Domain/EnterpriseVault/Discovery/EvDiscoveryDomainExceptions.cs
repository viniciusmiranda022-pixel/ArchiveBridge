namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Evidência de descoberta inconsistente/adulterada ou conjunto incompleto (fail-closed). Terminal: a
/// descoberta nunca repara silenciosamente nem sobrescreve — recusa.
/// </summary>
public sealed class EvDiscoveryEvidenceException(string message) : Exception(message);

/// <summary>
/// Inconsistência de persistência da descoberta (reserva ausente, hashes divergentes, promoção falhou).
/// Fail-closed e terminal — nunca grava um estado parcial.
/// </summary>
public sealed class EvDiscoveryPersistenceException(string message) : Exception(message);
