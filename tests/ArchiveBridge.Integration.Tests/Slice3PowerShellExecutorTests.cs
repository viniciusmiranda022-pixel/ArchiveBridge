using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 3 — validação FAIL-CLOSED do <see cref="EvProbeExecutor"/> ANTES de interpretar qualquer dado:
/// argumentos inseguros são recusados sem chamar o host; timeout, limite de saída, exit code ≠ 0, stderr
/// não vazio e stdout vazio falham fechado; o envelope versionado é validado propriedade a propriedade
/// (schema/probe/success/data) — nada ausente vira <c>false/0</c> — e o erro é classificado POR SONDA
/// (permissão negada não colapsa as demais).
/// </summary>
public sealed class Slice3ProbeExecutorTests
{
    private const int V = EvDiscoverySchema.ProbeEnvelopeVersion;

    private static string SuccessJson(string probe, string dataJson) =>
        $"{{\"schemaVersion\":{V},\"probe\":\"{probe}\",\"success\":true,\"errorCode\":null,\"data\":{dataJson}}}";

    private static async Task<EvProbeOutcome> RunAsync(
        EvPowerShellProbeKind kind, EvProbeExecution execution, IReadOnlyList<EvProbeArgument>? args = null)
    {
        var host = new StubHost(execution);
        var outcome = await new EvProbeExecutor(host).RunAsync(kind, args ?? [], TimeSpan.FromSeconds(5), 1024, CancellationToken.None);
        Assert.True(host.WasCalled); // o host só é chamado após os argumentos serem validados
        return outcome;
    }

    [Fact]
    public async Task ValidEnvelopeIsParsedAsSuccess()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec(SuccessJson("EvSite", "{\"count\":3}")));
        Assert.True(outcome.Ok);
        Assert.Equal(3, outcome.Data.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task NonZeroExitCodeFailsClosedEvenWithValidJson()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec(SuccessJson("EvSite", "{\"count\":3}"), exitCode: 1));
        AssertFailure(outcome, EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryFailed);
    }

    [Fact]
    public async Task NonEmptyStderrFailsClosedEvenWithValidStdout()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec(SuccessJson("EvSite", "{\"count\":3}"), stderr: "CategoryInfo: erro"));
        AssertFailure(outcome, EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryFailed);
    }

    [Fact]
    public async Task OutputLimitExceededFailsClosedEvenWithValidJson()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec(SuccessJson("EvSite", "{\"count\":3}"), outputLimitExceeded: true));
        AssertFailure(outcome, EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task TimedOutFailsClosed()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec(SuccessJson("EvSite", "{\"count\":3}"), timedOut: true));
        AssertFailure(outcome, EvErrorCategory.Timeout, EvDiscoveryResultCodes.DiscoveryTimeout);
    }

    [Fact]
    public async Task EmptyStdoutFailsClosed()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec(string.Empty));
        AssertFailure(outcome, EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task EmptyJsonObjectMissingRequiredPropertiesFailsClosed()
    {
        var outcome = await RunAsync(EvPowerShellProbeKind.EvSite, Exec("{}"));
        AssertFailure(outcome, EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task MissingRequiredSuccessPropertyFailsClosed()
    {
        var json = $"{{\"schemaVersion\":{V},\"probe\":\"EvSite\",\"errorCode\":null,\"data\":{{\"count\":3}}}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvSite, Exec(json)), EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task UnknownSchemaVersionFailsClosed()
    {
        var json = $"{{\"schemaVersion\":{V + 1},\"probe\":\"EvSite\",\"success\":true,\"errorCode\":null,\"data\":{{\"count\":3}}}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvSite, Exec(json)), EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task WrongTypeForSuccessFailsClosed()
    {
        var json = $"{{\"schemaVersion\":{V},\"probe\":\"EvSite\",\"success\":\"yes\",\"errorCode\":null,\"data\":{{\"count\":3}}}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvSite, Exec(json)), EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task ProbeNameMismatchFailsClosed()
    {
        // Envelope de outra sonda (probe divergente do tipo solicitado) ⇒ fail-closed.
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvSite, Exec(SuccessJson("EvServer", "{\"count\":3}"))), EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task SuccessWithNonObjectDataFailsClosed()
    {
        var json = $"{{\"schemaVersion\":{V},\"probe\":\"EvSite\",\"success\":true,\"errorCode\":null,\"data\":123}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvSite, Exec(json)), EvErrorCategory.OutputInvalid, EvDiscoveryResultCodes.DiscoveryOutputInvalid);
    }

    [Fact]
    public async Task PermissionDeniedIsClassifiedPerProbe()
    {
        var json = $"{{\"schemaVersion\":{V},\"probe\":\"EvArchive\",\"success\":false,\"errorCode\":\"PERMISSION_DENIED\",\"data\":{{}}}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvArchive, Exec(json)), EvErrorCategory.PermissionDenied, EvDiscoveryResultCodes.PermissionInsufficient);
    }

    [Fact]
    public async Task NotAvailableIsClassifiedAsIndeterminate()
    {
        var json = $"{{\"schemaVersion\":{V},\"probe\":\"EvArchive\",\"success\":false,\"errorCode\":\"NOT_AVAILABLE\",\"data\":{{}}}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvArchive, Exec(json)), EvErrorCategory.NotAvailable, EvDiscoveryResultCodes.CapabilityIndeterminate);
    }

    [Fact]
    public async Task UnknownErrorCodeIsExecutionFailed()
    {
        var json = $"{{\"schemaVersion\":{V},\"probe\":\"EvArchive\",\"success\":false,\"errorCode\":\"SOMETHING_ELSE\",\"data\":{{}}}}";
        AssertFailure(await RunAsync(EvPowerShellProbeKind.EvArchive, Exec(json)), EvErrorCategory.ExecutionFailed, EvDiscoveryResultCodes.DiscoveryFailed);
    }

    [Theory]
    [InlineData("a; Remove-Item")]
    [InlineData("a | Out-File")]
    [InlineData("$(Get-Process)")]
    [InlineData("${x}")]
    [InlineData("x`ny")]
    [InlineData("a && b")]
    [InlineData("x > out.txt")]
    [InlineData("Invoke-Expression 'x'")]
    public async Task UnsafeArgumentValueIsRejectedBeforeHostRuns(string value)
    {
        var host = new StubHost(Exec(SuccessJson("EvSite", "{\"count\":1}")));
        await Assert.ThrowsAsync<EvPowerShellUnsafeRequestException>(() =>
            new EvProbeExecutor(host).RunAsync(
                EvPowerShellProbeKind.EvSite, [new EvProbeArgument("Name", value)], TimeSpan.FromSeconds(5), 1024, CancellationToken.None));
        Assert.False(host.WasCalled); // recusado ANTES de qualquer execução
    }

    [Fact]
    public async Task InvalidArgumentNameIsRejectedBeforeHostRuns()
    {
        var host = new StubHost(Exec(SuccessJson("EvSite", "{\"count\":1}")));
        await Assert.ThrowsAsync<EvPowerShellUnsafeRequestException>(() =>
            new EvProbeExecutor(host).RunAsync(
                EvPowerShellProbeKind.EvSite, [new EvProbeArgument("1bad;name", "x")], TimeSpan.FromSeconds(5), 1024, CancellationToken.None));
        Assert.False(host.WasCalled);
    }

    private static EvProbeExecution Exec(
        string stdout, int exitCode = 0, string stderr = "", bool timedOut = false, bool outputLimitExceeded = false) =>
        new(exitCode, stdout, stderr, timedOut, outputLimitExceeded);

    private static void AssertFailure(EvProbeOutcome outcome, EvErrorCategory category, string resultCode)
    {
        Assert.False(outcome.Ok);
        Assert.Equal(category, outcome.Category);
        Assert.Equal(resultCode, outcome.ErrorCode!.Value.Value);
    }

    private sealed class StubHost(EvProbeExecution execution) : IEvPowerShellHost
    {
        private readonly EvProbeExecution _execution = execution;

        public bool WasCalled { get; private set; }

        public Task<EvProbeExecution> RunAsync(EvProbeRequest request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(_execution);
        }
    }
}

/// <summary>
/// Slice 3 — construtor do processo da sonda (<see cref="EvPowerShellCommandBuilder"/>) e catálogo de
/// scripts internos: PowerShell não interativo (<c>-NoProfile -NonInteractive</c>), script INTERNO fixo,
/// argumentos tipados via variável de ambiente (nunca na linha de comando), PATH restrito ao diretório do
/// executável, working directory controlado; nenhum script executa <c>Export-EVArchive</c> (no máximo lê
/// seus metadados) nem usa <c>Invoke-Expression</c>.
/// </summary>
public sealed class Slice3PowerShellCommandBuilderTests
{
    private static readonly string Exe = Path.Combine(Path.GetTempPath(), "pwsh-host", "pwsh");
    private static readonly string WorkDir = Path.Combine(Path.GetTempPath(), "pwsh-cwd");

    private static EvProbeRequest Request(EvPowerShellProbeKind kind, params (string Name, string Value)[] args) =>
        new(kind, [.. args.Select(a => new EvProbeArgument(a.Name, a.Value))], TimeSpan.FromSeconds(5), 4096);

    [Fact]
    public void BuildUsesNonInteractiveNoProfileFlagsAndFixedScript()
    {
        var startInfo = EvPowerShellCommandBuilder.Build(Request(EvPowerShellProbeKind.EvSite), Exe, WorkDir);
        Assert.Contains("-NoProfile", startInfo.ArgumentList);
        Assert.Contains("-NonInteractive", startInfo.ArgumentList);
        Assert.Contains("-NoLogo", startInfo.ArgumentList);
        Assert.Contains("-Command", startInfo.ArgumentList);
        Assert.Equal(EvProbeScriptCatalog.ScriptFor(EvPowerShellProbeKind.EvSite), startInfo.ArgumentList[^1]);
    }

    [Fact]
    public void BuildDoesNotPutArgumentValuesOnCommandLine()
    {
        const string secret = "SENSITIVE-ARG-VALUE";
        var startInfo = EvPowerShellCommandBuilder.Build(Request(EvPowerShellProbeKind.StagingPathAccess, ("StagingPath", secret)), Exe, WorkDir);

        Assert.DoesNotContain(startInfo.ArgumentList, arg => arg.Contains(secret, StringComparison.Ordinal));
        Assert.Equal(secret, startInfo.Environment[EvPowerShellCommandBuilder.ArgumentEnvironmentPrefix + "StagingPath"]);
    }

    [Fact]
    public void BuildRestrictsPathToExecutableDirectory()
    {
        var startInfo = EvPowerShellCommandBuilder.Build(Request(EvPowerShellProbeKind.EvSite), Exe, WorkDir);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(Exe)), startInfo.Environment["PATH"]);
    }

    [Fact]
    public void BuildControlsProcessSurface()
    {
        var startInfo = EvPowerShellCommandBuilder.Build(Request(EvPowerShellProbeKind.EvSite), Exe, WorkDir);
        Assert.Equal(WorkDir, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void EveryProbeKindHasAnImmutableFixedScriptEmittingTheVersionedEnvelope()
    {
        foreach (var kind in Enum.GetValues<EvPowerShellProbeKind>())
        {
            var script = EvProbeScriptCatalog.ScriptFor(kind);
            Assert.False(string.IsNullOrWhiteSpace(script));
            Assert.Contains("$ErrorActionPreference='Stop'", script, StringComparison.Ordinal);
            Assert.Contains("ConvertTo-Json", script, StringComparison.Ordinal);
            Assert.Contains("schemaVersion", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoProbeScriptExecutesExportButMetadataProbeReadsItsSignature()
    {
        foreach (var kind in Enum.GetValues<EvPowerShellProbeKind>())
        {
            var script = EvProbeScriptCatalog.ScriptFor(kind);
            if (kind == EvPowerShellProbeKind.ExportEvArchiveCommandMetadata)
            {
                // Só descobre METADADOS via Get-Command; nunca invoca a exportação.
                Assert.Contains("Get-Command -Name 'Export-EVArchive'", script, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("Export-EVArchive", script, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void UnknownProbeKindFailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EvProbeScriptCatalog.ScriptFor((EvPowerShellProbeKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvPowerShellCommandBuilder.Build(new EvProbeRequest((EvPowerShellProbeKind)999, [], TimeSpan.FromSeconds(1), 1024), Exe, WorkDir));
    }
}

/// <summary>
/// Slice 3 — <see cref="ByteLimitedProcessRunner"/>: contagem em BYTES reais (não em <c>string.Length</c>),
/// limite aplicado durante a leitura com o processo morto ao exceder, drenagem CONCORRENTE de stdout+stderr
/// (sem deadlock) e timeout que mata o processo. Portável — a prova usa utilitários POSIX no CI Linux.
/// </summary>
public sealed class Slice3ByteLimitedProcessRunnerTests
{
    // Utilitários POSIX (cat/yes/sleep) para exercitar o runner de forma portável; o CI de referência é Linux.
    // Fora de POSIX o caso não se aplica e retorna sem asserção (o runner em si é multiplataforma).
    private static readonly bool Posix = !OperatingSystem.IsWindows();

    [Fact]
    public async Task AsciiOutputUnderLimitIsCapturedFully()
    {
        if (!Posix)
        {
            return;
        }

        using var file = TempContent(Encoding.UTF8.GetBytes("hello world"));
        var result = await ByteLimitedProcessRunner.RunAsync(Cat(file.Path), TimeSpan.FromSeconds(15), maxOutputBytes: 1024, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputLimitExceeded);
        Assert.False(result.TimedOut);
        Assert.Equal("hello world", result.StandardOutput);
    }

    [Fact]
    public async Task LimitCountsBytesNotCharacters()
    {
        if (!Posix)
        {
            return;
        }
        const string content = "áé☕日本語"; // 6 chars, 16 bytes em UTF-8
        var bytes = Encoding.UTF8.GetBytes(content);
        Assert.True(bytes.Length > content.Length);
        using var file = TempContent(bytes);

        // Limite = nº de CARACTERES (< nº de bytes): se contasse chars não excederia; contando BYTES, excede.
        var byChars = await ByteLimitedProcessRunner.RunAsync(Cat(file.Path), TimeSpan.FromSeconds(15), content.Length, CancellationToken.None);
        Assert.True(byChars.OutputLimitExceeded);

        // Limite = nº exato de BYTES: captura completa, sem exceder.
        var byBytes = await ByteLimitedProcessRunner.RunAsync(Cat(file.Path), TimeSpan.FromSeconds(15), bytes.Length, CancellationToken.None);
        Assert.False(byBytes.OutputLimitExceeded);
        Assert.Equal(content, byBytes.StandardOutput);
    }

    [Fact]
    public async Task OutputExceedingLimitIsFlaggedAndCappedAtTheLimit()
    {
        if (!Posix)
        {
            return;
        }
        using var file = TempContent(Encoding.UTF8.GetBytes(new string('x', 5000)));
        var result = await ByteLimitedProcessRunner.RunAsync(Cat(file.Path), TimeSpan.FromSeconds(15), maxOutputBytes: 100, CancellationToken.None);
        Assert.True(result.OutputLimitExceeded);
        Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput) <= 100); // nunca aloca além do teto
    }

    [Fact]
    public async Task InfiniteOutputOnBothStreamsIsKilledWithoutDeadlock()
    {
        if (!Posix)
        {
            return;
        }
        // Produz saída infinita em stdout E stderr: a drenagem concorrente detecta o excesso e mata a árvore.
        var startInfo = Sh("yes & yes 1>&2 & wait");
        var result = await ByteLimitedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(30), maxOutputBytes: 200, CancellationToken.None);
        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.TimedOut); // encerrado pelo LIMITE, não pelo timeout (sem deadlock)
    }

    [Fact]
    public async Task TimeoutKillsTheProcess()
    {
        if (!Posix)
        {
            return;
        }
        var result = await ByteLimitedProcessRunner.RunAsync(Sh("sleep 30"), TimeSpan.FromMilliseconds(300), maxOutputBytes: 1024, CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.False(result.OutputLimitExceeded);
    }

    private static ProcessStartInfo Sh(string command)
    {
        var startInfo = new ProcessStartInfo { FileName = "/bin/sh" };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static ProcessStartInfo Cat(string path)
    {
        var startInfo = Sh("cat \"$AB_PROBE_FILE\"");
        startInfo.Environment["AB_PROBE_FILE"] = path;
        return startInfo;
    }

    private static TempFile TempContent(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "ab_byte_" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return new TempFile(path);
    }

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Melhor esforço.
            }
        }
    }
}
