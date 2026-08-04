using System.Buffers;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Executor PowerShell CONTROLADO: impõe a segurança ANTES de delegar ao <see cref="IEvPowerShellHost"/>
/// específico de SO. Valida a allowlist de comandos, nomes de parâmetro (identificadores), valores sem
/// metacaracteres de injeção (<c>; | &amp; ` $( ${</c>, nova linha, redirecionamento) e recusa
/// <c>Invoke-Expression</c>; exige timeout positivo; limita e sanitiza a saída. NUNCA aceita script
/// arbitrário, nunca concatena entrada em script e nunca registra credenciais. Os parâmetros são tipados
/// (o host os liga por nome, sem concatenação).
/// </summary>
public sealed partial class GuardedEvPowerShellExecutor : IEvPowerShellExecutor
{
    private readonly IEvPowerShellHost _host;
    private readonly HashSet<string> _allowed;
    private readonly long _maxOutputBytes;

    /// <summary>Cria o executor com o host, a allowlist e o limite de saída.</summary>
    public GuardedEvPowerShellExecutor(IEvPowerShellHost host, IReadOnlyList<string> allowedCommands, long maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(allowedCommands);
        if (maxOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes), "O limite de saída deve ser positivo.");
        }

        _host = host;
        AllowedCommands = allowedCommands.ToArray();
        _allowed = new HashSet<string>(AllowedCommands, StringComparer.OrdinalIgnoreCase);
        _maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AllowedCommands { get; }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNamePattern();

    [GeneratedRegex(@"(?i)(password|secret|token|pwd|sas|apikey|api[_-]?key)\s*[:=]\s*\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePattern();

    private static readonly SearchValues<char> ForbiddenValueChars = SearchValues.Create(";|&`\n\r\0<>");

    /// <inheritdoc />
    public async Task<EvPowerShellResult> InvokeAsync(EvPowerShellRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        EvPowerShellHostOutput output;
        try
        {
            output = await _host.RunAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Estouro do timeout (não cancelamento do chamador): fail-closed com TimedOut.
            return new EvPowerShellResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: true, OutputTruncated: false);
        }

        var (stdout, outTruncated) = LimitAndSanitize(output.StandardOutput);
        var (stderr, errTruncated) = LimitAndSanitize(output.StandardError);
        return new EvPowerShellResult(output.ExitCode, stdout, stderr, output.TimedOut, outTruncated || errTruncated);
    }

    private void Validate(EvPowerShellRequest request)
    {
        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new EvPowerShellUnsafeRequestException("timeout obrigatório e positivo");
        }

        if (string.Equals(request.CommandName, "Invoke-Expression", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.CommandName, "iex", StringComparison.OrdinalIgnoreCase))
        {
            throw new EvPowerShellUnsafeRequestException("Invoke-Expression é proibido");
        }

        if (!_allowed.Contains(request.CommandName))
        {
            throw new EvPowerShellNotAllowedException(request.CommandName);
        }

        ArgumentNullException.ThrowIfNull(request.Parameters);
        foreach (var parameter in request.Parameters)
        {
            if (parameter is null || !ParameterNamePattern().IsMatch(parameter.Name))
            {
                throw new EvPowerShellUnsafeRequestException("nome de parâmetro inválido");
            }

            var value = parameter.Value ?? string.Empty;
            if (value.AsSpan().IndexOfAny(ForbiddenValueChars) >= 0
                || value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("${", StringComparison.Ordinal)
                || value.Contains("Invoke-Expression", StringComparison.OrdinalIgnoreCase))
            {
                throw new EvPowerShellUnsafeRequestException("valor de parâmetro com metacaractere de injeção");
            }
        }
    }

    private (string Text, bool Truncated) LimitAndSanitize(string? raw)
    {
        var text = SensitivePattern().Replace(raw ?? string.Empty, "[REDACTED]");
        if (text.Length <= _maxOutputBytes)
        {
            return (text, false);
        }

        var limit = (int)Math.Min(_maxOutputBytes, int.MaxValue);
        return (text[..limit], true);
    }
}
