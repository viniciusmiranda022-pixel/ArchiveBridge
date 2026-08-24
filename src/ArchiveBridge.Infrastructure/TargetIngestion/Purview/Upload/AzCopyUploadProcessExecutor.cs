using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.PstProcessing;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Adapter isolado (worker dedicado) do binário AzCopy homologado (AB-I5-009 item 6/7). O caminho físico de
/// origem NUNCA é aceito de fora — é resolvido AQUI, pela MESMA fórmula canônica determinística
/// (<c>PartitionOutputBundleValidator.VersionDir</c>) já usada pelo verificador do Slice 4B, garantindo que
/// o arquivo transportado é EXATAMENTE o que acabou de ser fisicamente revalidado (item 12) — nunca um
/// caminho independente que pudesse divergir. O SAS só é revelado (<see cref="RedactedSecret.Reveal"/>)
/// NESTA classe, no menor escopo possível, para compor a URL de destino imediatamente antes de iniciar o
/// processo — nunca armazenado, logado ou devolvido (item 7).
/// </summary>
public sealed class AzCopyUploadProcessExecutor(AzCopyWorkerOptions options, PartitionExecutionOutputOptions outputOptions) : IAzCopyUploadExecutor
{
    private readonly AzCopyWorkerOptions _options = options;
    private readonly PartitionExecutionOutputOptions _outputOptions = outputOptions;

    /// <inheritdoc />
    public async Task<AzCopyBinaryIdentity> ProbeBinaryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
        {
            throw new AzCopyBinaryUnavailableException(
                "O binário AzCopy homologado configurado não foi encontrado (fail-closed).");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_options.ExecutablePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AzCopyBinaryUnavailableException("Falha ao ler o binário AzCopy homologado (fail-closed).", exception);
        }

        var hash = DeterministicHash.ComputeBytes(bytes);
        return new AzCopyBinaryIdentity(_options.DeclaredVersion, hash);
    }

    /// <inheritdoc />
    public async Task<AzCopyUploadFileResult> UploadFileAsync(AzCopyUploadFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // MESMA fórmula canônica determinística usada por LocalSinglePartExecutionVerifier — nunca um
        // caminho independente. A física já foi revalidada pela Application (item 12) antes de chegar
        // aqui; uma ausência a esta altura é uma condição de corrida rara (TOCTOU) e falha fechado.
        var versionDir = PartitionOutputBundleValidator.VersionDir(
            _outputOptions.RootPath, request.Scope.Tenant.Value, request.Scope.Project.Value,
            request.Execution.Plan.Value, request.Execution.PartKey.Value);
        var sourceFile = Path.Combine(versionDir, "part.pst");
        if (!File.Exists(sourceFile))
        {
            throw new InvalidOperationException(
                "O output canônico revalidado não foi encontrado no instante do transporte (condição de corrida rara) — fail-closed.");
        }

        var logDirectory = Path.Combine(
            _options.LogRoot, request.Scope.Tenant.Value.ToString("N"), request.Scope.Project.Value.ToString("N"),
            request.Attempt.Value.ToString("N"));
        Directory.CreateDirectory(logDirectory);

        var destinationUrl = ComposeDestinationUrl(request.ContainerSas, request.RemotePrefix, request.RemoteName);
        var startInfo = AzCopyProcessArgumentBuilder.Build(_options.ExecutablePath, sourceFile, destinationUrl, logDirectory);

        var result = await ByteLimitedProcessRunner
            .RunAsync(startInfo, request.Timeout, _options.MaxOutputBytes, cancellationToken)
            .ConfigureAwait(false);

        // stdout/stderr NUNCA propagam além deste ponto (item 10 — poderiam refletir o SAS/path sensível).
        return new AzCopyUploadFileResult(result.ExitCode, result.TimedOut, result.OutputLimitExceeded);
    }

    // ÚNICO ponto de revelação do SAS neste adapter — compõe a URL de destino e a devolve para uso
    // IMEDIATO pelo builder de ProcessStartInfo; o valor nunca é atribuído a um campo, log ou exceção.
    private static string ComposeDestinationUrl(RedactedSecret containerSas, PurviewRemoteUploadPrefix remotePrefix, PurviewRemotePstName remoteName)
    {
        var raw = containerSas.Reveal();
        var uri = new Uri(raw, UriKind.Absolute);
        var basePath = uri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(uri)
        {
            Path = $"{basePath}/{remotePrefix.WaveSegment}/{remoteName.Value}",
        };
        return builder.Uri.ToString();
    }
}
