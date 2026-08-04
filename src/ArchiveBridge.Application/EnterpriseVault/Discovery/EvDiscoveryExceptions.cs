namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>Erro de validação/domínio da descoberta (terminal: falha o Job de forma fechada).</summary>
public sealed class EvDiscoveryValidationException(string message) : Exception(message);

/// <summary>Recurso exigido pela descoberta não encontrado no escopo (terminal).</summary>
public sealed class EvDiscoveryNotFoundException(string message) : Exception(message);

/// <summary>
/// Contexto do comando de descoberta OBSOLETO (a versão/estado que o originou mudou). Terminal:
/// <c>STALE_COMMAND_CONTEXT</c> — o worker nunca atua silenciosamente sobre uma versão diferente.
/// </summary>
public sealed class StaleEvDiscoveryContextException(string message) : Exception(message);

/// <summary>Falha transitória de sonda (conectividade/timeout/saída inválida) — elegível a retry.</summary>
public sealed class EvDiscoveryProbeException(string message) : Exception(message);
