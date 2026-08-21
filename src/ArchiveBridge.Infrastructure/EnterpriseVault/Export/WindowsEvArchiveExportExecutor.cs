using System.Text.Json;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Adapter CONCRETO do exporter Enterprise Vault (AB-4C-005 item 6): resolve o diretório de saída SOB a
/// raiz autorizada do connector (fail-closed em traversal/UNC/reparse), monta o processo PowerShell com
/// argumentos TIPADOS via variável de ambiente (item 7, sem command injection) e executa com captura
/// byte-limitada (reaproveita <see cref="ByteLimitedProcessRunner"/> do Slice 3). Só é suportado em um
/// Windows Worker — a prova contra Enterprise Vault REAL é pendente de laboratório (STOP-THE-LINE: nenhum
/// teste deste Passo executa contra um EV real). Fora do Windows, lança
/// <see cref="PlatformNotSupportedException"/> explícito.
/// </summary>
public sealed class WindowsEvArchiveExportExecutor(string powerShellExecutable, string workingDirectory) : IEvArchiveExportExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _powerShellExecutable = powerShellExecutable;
    private readonly string _workingDirectory = workingDirectory;

    /// <inheritdoc />
    public async Task<EvExportProcessResult> RunAsync(EvExportProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "A exportação real do Enterprise Vault requer um Windows Worker; a prova contra EV real é pendente de laboratório.");
        }

        // Fail-closed ANTES de qualquer processo: o diretório de saída precisa estar contido na raiz
        // autorizada do connector (item 9). Nenhum processo é iniciado se a resolução falhar.
        var resolvedDirectory = EvExportOutputPaths.ResolvePhysicalDirectory(request.OutputLocation);

        var startInfo = EvExportProcessArgumentBuilder.Build(request, _powerShellExecutable, _workingDirectory, resolvedDirectory);
        var run = await ByteLimitedProcessRunner
            .RunAsync(startInfo, request.Timeout, request.MaxOutputBytes, cancellationToken).ConfigureAwait(false);

        if (run.TimedOut)
        {
            return new EvExportProcessResult(run.ExitCode, TimedOut: true, OutputLimitExceeded: false, "", request.ConnectorVersion, [], "TIMEOUT");
        }

        if (run.OutputLimitExceeded)
        {
            return new EvExportProcessResult(
                run.ExitCode, TimedOut: false, OutputLimitExceeded: true, "", request.ConnectorVersion, [], "OUTPUT_LIMIT_EXCEEDED");
        }

        return ParseEnvelope(run.ExitCode, run.StandardOutput, request.ConnectorVersion);
    }

    private static EvExportProcessResult ParseEnvelope(int exitCode, string stdout, string connectorVersion)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new EvExportProcessResult(exitCode, TimedOut: false, OutputLimitExceeded: false, "", connectorVersion, [], "PROCESS_EXIT_NON_ZERO");
        }

        ExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ExportEnvelope>(stdout, JsonOptions);
        }
        catch (JsonException)
        {
            return new EvExportProcessResult(exitCode, TimedOut: false, OutputLimitExceeded: false, "", connectorVersion, [], "OUTPUT_INVALID_JSON");
        }

        if (envelope is null || envelope.SchemaVersion is null || envelope.Success is null
            || envelope.SchemaVersion.Value != EvExportProcessScript.EnvelopeSchemaVersion)
        {
            return new EvExportProcessResult(exitCode, TimedOut: false, OutputLimitExceeded: false, "", connectorVersion, [], "OUTPUT_ENVELOPE_INVALID");
        }

        if (!envelope.Success.Value)
        {
            return new EvExportProcessResult(1, TimedOut: false, OutputLimitExceeded: false, "", connectorVersion, [], envelope.ErrorCode ?? "EXPORT_FAILED");
        }

        var oversized = (envelope.OversizedItems ?? [])
            .Select(item => new EvOversizedNativeItemRaw(item.ExternalItemReference ?? string.Empty, item.SizeBytes ?? 0, item.Diagnostic))
            .Where(item => !string.IsNullOrWhiteSpace(item.ExternalItemReference))
            .ToArray();

        return new EvExportProcessResult(0, TimedOut: false, OutputLimitExceeded: false, envelope.EngineVersion ?? "", connectorVersion, oversized, null);
    }

    private sealed record ExportEnvelope(
        int? SchemaVersion, bool? Success, string? ErrorCode, string? EngineVersion, List<OversizedItemEnvelope>? OversizedItems);

    private sealed record OversizedItemEnvelope(string? ExternalItemReference, long? SizeBytes, string? Diagnostic);
}
