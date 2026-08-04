using System.Diagnostics;
using System.Text;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>Execução bruta de um processo com captura byte-limitada.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputLimitExceeded);

/// <summary>
/// Executa um processo drenando stdout e stderr CONCORRENTEMENTE (sem deadlock), contando BYTES reais
/// (UTF-8), com limite POR STREAM: ao exceder o limite o processo é morto (árvore inteira) e
/// <see cref="ProcessRunResult.OutputLimitExceeded"/> é sinalizado; ao estourar o timeout o processo é
/// morto e <see cref="ProcessRunResult.TimedOut"/> é sinalizado. Nunca aloca a saída completa antes de
/// checar o limite (buffer limitado ao teto). Portável (independe de Enterprise Vault) e testável.
/// </summary>
public static class ByteLimitedProcessRunner
{
    /// <summary>Roda o processo descrito por <paramref name="startInfo"/> com timeout e limite de bytes por stream.</summary>
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, long maxOutputBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Falha ao iniciar o processo de sonda.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, maxOutputBytes, timeoutCts.Token);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, maxOutputBytes, timeoutCts.Token);

        var timedOut = false;
        (byte[] Bytes, bool Exceeded) outResult = ([], false);
        (byte[] Bytes, bool Exceeded) errResult = ([], false);
        try
        {
            outResult = await stdoutTask.ConfigureAwait(false);
            errResult = await stderrTask.ConfigureAwait(false);
            if (outResult.Exceeded || errResult.Exceeded)
            {
                Kill(process);
            }
            else
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            Kill(process);
            outResult = await SafeAwait(stdoutTask).ConfigureAwait(false);
            errResult = await SafeAwait(stderrTask).ConfigureAwait(false);
        }

        var exceeded = outResult.Exceeded || errResult.Exceeded;
        var exitCode = TryExitCode(process);
        return new ProcessRunResult(
            exitCode, Encoding.UTF8.GetString(outResult.Bytes), Encoding.UTF8.GetString(errResult.Bytes), timedOut, exceeded);
    }

    private static async Task<(byte[] Bytes, bool Exceeded)> DrainAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var captured = new MemoryStream();
        long total = 0;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                break; // timeout, processo morto ou stream fechado
            }

            if (read == 0)
            {
                break;
            }

            total += read;
            var room = maxBytes - captured.Length;
            if (room > 0)
            {
                captured.Write(buffer, 0, (int)Math.Min(read, room));
            }

            if (total > maxBytes)
            {
                return (captured.ToArray(), true); // excedeu o limite — sinaliza para o chamador matar o processo
            }
        }

        return (captured.ToArray(), false);
    }

    private static async Task<(byte[] Bytes, bool Exceeded)> SafeAwait(Task<(byte[] Bytes, bool Exceeded)> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return ([], false);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Best-effort: o processo pode já ter saído.
        }
    }

    private static int TryExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
