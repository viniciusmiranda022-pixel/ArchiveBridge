using System.Diagnostics;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Constrói o <see cref="ProcessStartInfo"/> de UM upload de arquivo via AzCopy (runbook §25.6, AB-I5-009
/// item 6/7): usa <c>ProcessStartInfo.ArgumentList</c> EXCLUSIVAMENTE — nunca concatenação de string de
/// shell. <c>AZCOPY_LOG_LOCATION</c>/<c>AZCOPY_JOB_PLAN_LOCATION</c> apontam para o diretório server-side
/// DEDICADO da tentativa. Como o SAS inevitavelmente aparece no command line do processo AzCopy (item 7),
/// esta classe é o ÚNICO ponto do worker que recebe a URL de destino já composta — nunca a registra, loga
/// ou devolve; o <see cref="ProcessStartInfo"/> resultante é consumido imediatamente pelo runner de
/// processo e descartado.
/// </summary>
internal static class AzCopyProcessArgumentBuilder
{
    /// <summary>Monta o processo de cópia de UM arquivo para o destino SAS já composto.</summary>
    public static ProcessStartInfo Build(string azCopyExecutable, string sourceFilePath, string destinationUrl, string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(azCopyExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = azCopyExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // API segura (ArgumentList), nunca concatenação de string de shell. O destino (com o SAS) é o
        // ÚNICO valor sensível — inevitavelmente no command line do processo (item 7), confinado a este
        // worker dedicado, nunca escrito em log/evidence/exception.
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add(sourceFilePath);
        startInfo.ArgumentList.Add(destinationUrl);

        // Ambiente controlado: PATH restrito ao diretório do executável; log/plan sempre no diretório
        // server-side dedicado por tentativa (nunca um caminho fornecido pelo caller).
        startInfo.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(azCopyExecutable)) ?? string.Empty;
        startInfo.Environment["AZCOPY_LOG_LOCATION"] = logDirectory;
        startInfo.Environment["AZCOPY_JOB_PLAN_LOCATION"] = logDirectory;
        startInfo.Environment["AZCOPY_LOG_LEVEL"] = "INFO";

        return startInfo;
    }
}
