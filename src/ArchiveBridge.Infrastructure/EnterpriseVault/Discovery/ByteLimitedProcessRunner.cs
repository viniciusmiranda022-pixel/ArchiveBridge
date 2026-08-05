using System.Diagnostics;
using System.Text;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>Execução bruta de um processo com captura byte-limitada.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputLimitExceeded);

/// <summary>
/// Executa um processo drenando stdout e stderr CONCORRENTEMENTE (sem deadlock), contando BYTES reais
/// (UTF-8), com limite POR STREAM. Ao exceder o limite em QUALQUER stream, o processo é morto (árvore
/// inteira) IMEDIATAMENTE — sem esperar o outro stream terminar — via um <see cref="CancellationTokenSource"/>
/// compartilhado que também interrompe a leitura do stream irmão. O timeout e o cancelamento do chamador
/// também matam a árvore. O término do processo é SEMPRE aguardado e um <c>finally</c> garante o encerramento
/// (nenhum processo órfão). Nunca aloca a saída completa antes de checar o limite (buffer limitado ao teto).
/// Portável (independe de Enterprise Vault) e testável.
/// </summary>
public static class ByteLimitedProcessRunner
{
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(30);

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

        // timeoutCts: timeout + cancelamento do chamador. killCts: também disparado assim que QUALQUER stream
        // excede — cancela a leitura de AMBOS os streams para matar já, sem esperar o irmão terminar.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var killCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

        var stdoutExceeded = 0;
        var stderrExceeded = 0;

        var outTask = DrainAsync(process.StandardOutput.BaseStream, maxOutputBytes, killCts, () => Interlocked.Exchange(ref stdoutExceeded, 1));
        var errTask = DrainAsync(process.StandardError.BaseStream, maxOutputBytes, killCts, () => Interlocked.Exchange(ref stderrExceeded, 1));

        try
        {
            var outBytes = await outTask.ConfigureAwait(false);
            var errBytes = await errTask.ConfigureAwait(false);
            var exceeded = stdoutExceeded == 1 || stderrExceeded == 1;

            // Mata a árvore ANTES de aguardar o término quando excedeu o limite, estourou o timeout ou o
            // chamador cancelou (todos refletidos em timeoutCts). O término é sempre aguardado.
            if (exceeded || timeoutCts.IsCancellationRequested)
            {
                Kill(process);
            }

            await WaitForExitAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); // cancelamento do chamador ⇒ propaga (árvore já morta)

            var timedOut = timeoutCts.IsCancellationRequested && !exceeded;
            return new ProcessRunResult(
                TryExitCode(process), Encoding.UTF8.GetString(outBytes), Encoding.UTF8.GetString(errBytes), timedOut, exceeded);
        }
        finally
        {
            Kill(process); // cleanup garantido: nenhum processo órfão
        }
    }

    private static async Task<byte[]> DrainAsync(
        Stream stream, long maxBytes, CancellationTokenSource killCts, Action markExceeded)
    {
        var buffer = new byte[8192];
        using var captured = new MemoryStream();
        long total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, killCts.Token).ConfigureAwait(false);
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
                    markExceeded();
                    // Mata JÁ: cancela a leitura do stream irmão (sinaliza o kill) sem esperá-lo terminar.
                    await killCts.CancelAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // timeout, kill por limite/cancelamento ou stream fechado: devolve o que capturou até aqui.
        }

        return captured.ToArray();
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using var graceCts = new CancellationTokenSource(ExitGrace);
        try
        {
            await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // O processo já saiu, é inacessível, ou não terminou dentro da carência — o finally reforça o kill.
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
