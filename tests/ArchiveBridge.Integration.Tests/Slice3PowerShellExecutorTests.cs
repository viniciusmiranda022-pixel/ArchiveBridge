using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 3 — SEGURANÇA do executor PowerShell controlado: allowlist, recusa de Invoke-Expression, recusa
/// de valores com metacaracteres de injeção, nomes de parâmetro válidos, timeout, limite e sanitização de
/// saída. Todos os casos inseguros são recusados ANTES de qualquer execução (o host nunca é chamado).
/// </summary>
public sealed class Slice3PowerShellExecutorTests
{
    private static readonly IReadOnlyList<string> Allowlist = ["Get-Command", "Get-EvEnvironmentSnapshot"];

    private static GuardedEvPowerShellExecutor Executor(RecordingHost host, long maxOutput = 1024) =>
        new(host, Allowlist, maxOutput);

    private static EvPowerShellRequest Request(string command, params (string Name, string Value)[] parameters) =>
        new(command, parameters.Select(p => new EvPowerShellParameter(p.Name, p.Value)).ToArray(), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task CommandNotOnAllowlistIsRejectedWithoutRunning()
    {
        var host = new RecordingHost();
        await Assert.ThrowsAsync<EvPowerShellNotAllowedException>(() =>
            Executor(host).InvokeAsync(Request("Remove-Item", ("Path", "x")), CancellationToken.None));
        Assert.False(host.WasCalled);
    }

    [Fact]
    public async Task InvokeExpressionIsRejected()
    {
        var host = new RecordingHost();
        await Assert.ThrowsAsync<EvPowerShellUnsafeRequestException>(() =>
            Executor(host).InvokeAsync(Request("Invoke-Expression", ("Command", "x")), CancellationToken.None));
        Assert.False(host.WasCalled);
    }

    [Theory]
    [InlineData("a; Remove-Item")]
    [InlineData("a | iex")]
    [InlineData("$(Get-Process)")]
    [InlineData("`n whoami")]
    [InlineData("a && b")]
    [InlineData("x > out.txt")]
    public async Task MaliciousParameterValueIsRejected(string maliciousValue)
    {
        var host = new RecordingHost();
        await Assert.ThrowsAsync<EvPowerShellUnsafeRequestException>(() =>
            Executor(host).InvokeAsync(Request("Get-Command", ("Name", maliciousValue)), CancellationToken.None));
        Assert.False(host.WasCalled);
    }

    [Fact]
    public async Task InvalidParameterNameIsRejected()
    {
        var host = new RecordingHost();
        await Assert.ThrowsAsync<EvPowerShellUnsafeRequestException>(() =>
            Executor(host).InvokeAsync(Request("Get-Command", ("1bad;name", "x")), CancellationToken.None));
        Assert.False(host.WasCalled);
    }

    [Fact]
    public async Task ZeroTimeoutIsRejected()
    {
        var host = new RecordingHost();
        var request = new EvPowerShellRequest("Get-Command", [], TimeSpan.Zero);
        await Assert.ThrowsAsync<EvPowerShellUnsafeRequestException>(() => Executor(host).InvokeAsync(request, CancellationToken.None));
        Assert.False(host.WasCalled);
    }

    [Fact]
    public async Task ValidRequestRunsAndSanitizesAndLimitsOutput()
    {
        var host = new RecordingHost { Output = new EvPowerShellHostOutput(0, "user password=SECRET-VALUE tail", string.Empty, false) };
        var result = await Executor(host).InvokeAsync(Request("Get-Command", ("Name", "Export-EVArchive")), CancellationToken.None);
        Assert.True(host.WasCalled);
        Assert.DoesNotContain("SECRET-VALUE", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedOutputIsTruncated()
    {
        var host = new RecordingHost { Output = new EvPowerShellHostOutput(0, new string('x', 5000), string.Empty, false) };
        var result = await Executor(host, maxOutput: 100).InvokeAsync(Request("Get-Command", ("Name", "x")), CancellationToken.None);
        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 100);
    }

    [Fact]
    public async Task TimeoutYieldsTimedOutResult()
    {
        var host = new RecordingHost { Delay = TimeSpan.FromSeconds(30) };
        var request = new EvPowerShellRequest("Get-Command", [new EvPowerShellParameter("Name", "x")], TimeSpan.FromMilliseconds(80));
        var result = await new GuardedEvPowerShellExecutor(host, Allowlist, 1024).InvokeAsync(request, CancellationToken.None);
        Assert.True(result.TimedOut);
    }

    private sealed class RecordingHost : IEvPowerShellHost
    {
        public bool WasCalled { get; private set; }

        public EvPowerShellHostOutput Output { get; set; } = new(0, string.Empty, string.Empty, false);

        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<EvPowerShellHostOutput> RunAsync(EvPowerShellRequest request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            return Output;
        }
    }
}
