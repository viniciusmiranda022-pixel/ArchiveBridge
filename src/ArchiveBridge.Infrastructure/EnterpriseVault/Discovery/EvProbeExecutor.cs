using System.Text.Json;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>Resultado validado de uma sonda: sucesso com os dados (JSON) ou falha classificada.</summary>
public sealed record EvProbeOutcome(bool Ok, JsonElement Data, EvErrorCategory Category, EvDiscoveryResultCode? ErrorCode, string Detail)
{
    /// <summary>Sonda bem-sucedida com o payload de dados validado.</summary>
    public static EvProbeOutcome Success(JsonElement data) => new(true, data, EvErrorCategory.None, null, string.Empty);

    /// <summary>Sonda falhada de forma classificada (fail-closed).</summary>
    public static EvProbeOutcome Failure(EvErrorCategory category, string resultCode, string detail) =>
        new(false, default, category, new EvDiscoveryResultCode(resultCode), detail);
}

/// <summary>
/// Executor de sonda com validação FAIL-CLOSED, ANTES de interpretar qualquer dado: valida os argumentos
/// (sem metacaracteres de injeção), delega ao <see cref="IEvPowerShellHost"/> e então recusa
/// <c>TimedOut</c>, limite de saída excedido, <c>ExitCode != 0</c>, <c>stderr</c> não vazio (política
/// explícita: fecha), <c>stdout</c> vazio e envelope inválido. O envelope é VERSIONADO
/// (<c>schemaVersion/probe/success/errorCode/data</c>) e todas as propriedades obrigatórias são validadas
/// — nenhuma propriedade ausente vira <c>false/0</c> silenciosamente. Cada sonda devolve seu próprio
/// resultado (falha de uma não colapsa as demais).
/// </summary>
public sealed partial class EvProbeExecutor(IEvPowerShellHost host)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IEvPowerShellHost _host = host;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentNamePattern();

    private static readonly System.Buffers.SearchValues<char> ForbiddenValueChars =
        System.Buffers.SearchValues.Create(";|&`\n\r\0<>");

    /// <summary>Executa a sonda tipada e devolve o resultado validado (fail-closed).</summary>
    public async Task<EvProbeOutcome> RunAsync(
        EvPowerShellProbeKind kind, IReadOnlyList<EvProbeArgument> arguments, TimeSpan timeout, long maxOutputBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ValidateArguments(arguments);

        var execution = await _host.RunAsync(new EvProbeRequest(kind, arguments, timeout, maxOutputBytes), cancellationToken).ConfigureAwait(false);

        if (execution.TimedOut)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.Timeout, EvDiscoveryResultCodes.DiscoveryTimeout, "timeout da sonda");
        }

        if (execution.OutputLimitExceeded)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "limite de saída excedido");
        }

        if (execution.ExitCode != 0)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryFailed, "exit code não zero");
        }

        if (!string.IsNullOrEmpty(execution.StandardError))
        {
            // Política explícita: stderr não vazio ⇒ fail-closed (a sonda deve reportar erro via envelope, não stderr).
            return EvProbeOutcome.Failure(EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryFailed, "stderr não vazio");
        }

        if (string.IsNullOrWhiteSpace(execution.StandardOutput))
        {
            return EvProbeOutcome.Failure(EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "stdout vazio");
        }

        return ParseEnvelope(kind, execution.StandardOutput);
    }

    private static EvProbeOutcome ParseEnvelope(EvPowerShellProbeKind kind, string stdout)
    {
        EvProbeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EvProbeEnvelope>(stdout, JsonOptions);
        }
        catch (JsonException)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "JSON inválido");
        }

        if (envelope is null || envelope.SchemaVersion is null || envelope.Probe is null || envelope.Success is null)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "envelope incompleto");
        }

        if (envelope.SchemaVersion.Value != EvDiscoverySchema.ProbeEnvelopeVersion)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "schema desconhecido");
        }

        if (!string.Equals(envelope.Probe, kind.ToString(), StringComparison.Ordinal))
        {
            return EvProbeOutcome.Failure(EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "probe divergente");
        }

        if (!envelope.Success.Value)
        {
            var (category, code) = ClassifyProbeError(envelope.ErrorCode);
            return EvProbeOutcome.Failure(category, code, envelope.ErrorCode ?? "erro de sonda");
        }

        if (envelope.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return EvProbeOutcome.Failure(EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "data ausente");
        }

        return EvProbeOutcome.Success(data);
    }

    private static (EvErrorCategory Category, string Code) ClassifyProbeError(string? errorCode) => errorCode switch
    {
        "PERMISSION_DENIED" => (EvErrorCategory.PermissionDenied, EvDiscoveryResultCodes.PermissionInsufficient),
        "NOT_AVAILABLE" => (EvErrorCategory.NotAvailable, EvDiscoveryResultCodes.CapabilityIndeterminate),
        _ => (EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryFailed),
    };

    private static void ValidateArguments(IReadOnlyList<EvProbeArgument> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument is null || !ArgumentNamePattern().IsMatch(argument.Name))
            {
                throw new EvPowerShellUnsafeRequestException("nome de argumento inválido");
            }

            var value = argument.Value ?? string.Empty;
            if (value.AsSpan().IndexOfAny(ForbiddenValueChars) >= 0
                || value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("${", StringComparison.Ordinal)
                || value.Contains("Invoke-Expression", StringComparison.OrdinalIgnoreCase))
            {
                throw new EvPowerShellUnsafeRequestException("valor de argumento com metacaractere de injeção");
            }
        }
    }

    private sealed record EvProbeEnvelope(int? SchemaVersion, string? Probe, bool? Success, string? ErrorCode, JsonElement? Data);
}
