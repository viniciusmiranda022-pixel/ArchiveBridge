namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>Um parâmetro TIPADO de um comando PowerShell (nome + valor), nunca concatenado em script.</summary>
public sealed record EvPowerShellParameter(string Name, string Value);

/// <summary>
/// Requisição de execução PowerShell CONTROLADA: um comando da allowlist com parâmetros tipados e um
/// timeout obrigatório. NUNCA carrega script arbitrário — o executor liga os parâmetros de forma tipada
/// (sem <c>Invoke-Expression</c>, sem concatenação de entrada em script).
/// </summary>
public sealed record EvPowerShellRequest(
    string CommandName,
    IReadOnlyList<EvPowerShellParameter> Parameters,
    TimeSpan Timeout);

/// <summary>
/// Resultado de uma execução PowerShell: stdout/stderr/exit code capturados separadamente, já com
/// tamanho limitado e sanitizados de informação sensível. <see cref="TimedOut"/> indica estouro do
/// timeout; <see cref="OutputTruncated"/> indica que a saída excedeu o limite e foi truncada.
/// </summary>
public sealed record EvPowerShellResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated);

/// <summary>
/// Saída bruta de um host PowerShell de baixo nível (específico de SO). Recebe uma requisição JÁ
/// validada (comando na allowlist, parâmetros tipados/sanitizados) — o host apenas executa.
/// </summary>
public sealed record EvPowerShellHostOutput(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Comando PowerShell rejeitado por não estar na allowlist (fail-closed).</summary>
public sealed class EvPowerShellNotAllowedException(string commandName)
    : Exception($"Comando PowerShell fora da allowlist recusado: '{commandName}'.")
{
    /// <summary>Nome do comando recusado.</summary>
    public string CommandName { get; } = commandName;
}

/// <summary>Requisição PowerShell rejeitada por conteúdo inseguro (injeção/caractere proibido).</summary>
public sealed class EvPowerShellUnsafeRequestException(string reason)
    : Exception($"Requisição PowerShell recusada (insegura): {reason}.");

/// <summary>
/// Porta CONTROLADA de execução PowerShell usada pela descoberta. A implementação impõe a allowlist de
/// comandos, timeout obrigatório, parâmetros tipados (sem script arbitrário, sem <c>Invoke-Expression</c>,
/// sem concatenação de entrada), limite de tamanho de saída, sanitização e working directory controlado.
/// Nunca armazena senha/token/credencial/conteúdo de mensagem/PST. Compatível com execução em Windows
/// Worker isolado por meio de um <see cref="IEvPowerShellHost"/>.
/// </summary>
public interface IEvPowerShellExecutor
{
    /// <summary>Comandos permitidos (allowlist explícita). Qualquer outro é recusado.</summary>
    IReadOnlyList<string> AllowedCommands { get; }

    /// <summary>
    /// Executa a requisição de forma não interativa após validá-la (allowlist + parâmetros seguros +
    /// timeout). Lança <see cref="EvPowerShellNotAllowedException"/>/<see cref="EvPowerShellUnsafeRequestException"/>
    /// ANTES de qualquer execução quando a requisição é inválida (fail-closed).
    /// </summary>
    Task<EvPowerShellResult> InvokeAsync(EvPowerShellRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Host PowerShell de BAIXO NÍVEL (específico de SO; Windows em produção, fixture nos testes). Só é
/// chamado pelo executor controlado, com uma requisição já validada. Os testes de CI NÃO fingem possuir
/// um Enterprise Vault real: injetam um host de fixture que devolve saídas controladas.
/// </summary>
public interface IEvPowerShellHost
{
    /// <summary>Executa a requisição já validada e devolve a saída bruta (sem interpretar).</summary>
    Task<EvPowerShellHostOutput> RunAsync(EvPowerShellRequest request, CancellationToken cancellationToken);
}
