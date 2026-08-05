using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>Argumento TIPADO de uma sonda (nome + valor), ligado como variável de ambiente — nunca concatenado em script.</summary>
public sealed record EvProbeArgument(string Name, string Value);

/// <summary>
/// Requisição de uma sonda TIPADA (<see cref="EvPowerShellProbeKind"/>) com argumentos tipados, timeout e
/// limite de saída em BYTES. NÃO carrega CommandName arbitrário nem script — o host mapeia a operação para
/// um script interno fixo e imutável.
/// </summary>
public sealed record EvProbeRequest(
    EvPowerShellProbeKind Kind,
    IReadOnlyList<EvProbeArgument> Arguments,
    TimeSpan Timeout,
    long MaxOutputBytes);

/// <summary>
/// Execução BRUTA de uma sonda: exit code + stdout/stderr (capturados com limite de BYTES) + sinais de
/// timeout e de limite excedido. <see cref="OutputLimitExceeded"/> indica que o processo foi interrompido
/// por exceder o limite de bytes.
/// </summary>
public sealed record EvProbeExecution(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputLimitExceeded);

/// <summary>Requisição de sonda rejeitada por conteúdo inseguro (injeção/caractere proibido) — fail-closed.</summary>
public sealed class EvPowerShellUnsafeRequestException(string reason)
    : Exception($"Requisição de sonda recusada (insegura): {reason}.");

/// <summary>
/// Host PowerShell de BAIXO NÍVEL (específico de SO; Windows Worker em produção, fixture nos testes).
/// Recebe uma sonda TIPADA e a mapeia para um script interno FIXO, executado de forma não interativa
/// (<c>-NoProfile -NonInteractive</c>), com timeout, working directory e ambiente controlados, PATH
/// controlado, captura separada e byte-limitada de stdout/stderr, sem credencial em command line, sem
/// <c>Invoke-Expression</c> e sem concatenação de entrada em script. Os testes de CI injetam um host de
/// fixture (sem Enterprise Vault real).
/// </summary>
public interface IEvPowerShellHost
{
    /// <summary>Executa a sonda tipada e devolve a execução bruta (byte-limitada). Não interpreta a saída.</summary>
    Task<EvProbeExecution> RunAsync(EvProbeRequest request, CancellationToken cancellationToken);
}
