using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArchiveBridge.Contracts.EnterpriseVault.Export;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Script PowerShell INTERNO E FIXO que invoca <c>Export-EVArchive</c> (runbook §16.3, AB-4C-005 item 7).
/// O texto do script NUNCA é concatenado com dados da requisição — é uma constante imutável. Todo dado da
/// requisição (<c>ArchiveId</c>, <c>OutputDirectory</c>, <c>MaxThreads</c>, <c>MaxPSTSizeMB</c>) entra
/// EXCLUSIVAMENTE via variáveis de ambiente tipadas (<c>EV_EXPORT_ARG_*</c>), lidas pelo script com
/// <c>$env:</c> e convertidas para o tipo correto (<c>[int]</c>) — nunca interpoladas na linha de comando
/// nem reinterpretadas como código (nenhum <c>Invoke-Expression</c>/<c>iex</c>). Isso torna a superfície de
/// command injection estruturalmente vazia: mesmo um valor contendo <c>;</c>, aspas, backticks, pipes,
/// CR/LF ou <c>$()</c> permanece apenas como o CONTEÚDO de texto de uma variável de ambiente — nunca é
/// reanalisado pelo interpretador PowerShell. Mesmo padrão já hardenado em
/// <c>EvProbeScriptCatalog</c>/<c>EvPowerShellCommandBuilder</c> (Discovery, Slice 3).
/// </summary>
public static class EvExportProcessScript
{
    /// <summary>Versão do envelope JSON emitido pelo script — um schema desconhecido é recusado na leitura.</summary>
    public const int EnvelopeSchemaVersion = 1;

    /// <summary>
    /// O único script executado: cria o diretório de saída, invoca <c>Export-EVArchive</c> com os
    /// parâmetros TIPADOS lidos do ambiente (nunca da linha de comando) e emite um envelope JSON
    /// estruturado (nunca transcript bruto) com o resultado — sucesso/erro classificado, versão do engine
    /// observada e os itens oversized relatados pelo cmdlet (§16.4).
    /// </summary>
    public const string Script =
        """
        $ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest; $ov = 1
        $archiveId = $env:EV_EXPORT_ARG_ArchiveId
        $outputDirectory = $env:EV_EXPORT_ARG_OutputDirectory
        $maxThreads = [int]$env:EV_EXPORT_ARG_MaxThreads
        $maxPstSizeMb = [int]$env:EV_EXPORT_ARG_MaxPstSizeMb
        try {
            New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
            Export-EVArchive -ArchiveId $archiveId -OutputDirectory $outputDirectory -Format PST -MaxThreads $maxThreads -MaxPSTSizeMB $maxPstSizeMb -Retry
            $cmd = Get-Command -Name 'Export-EVArchive'
            $engineVersion = "$($cmd.Version)"
            @{schemaVersion=$ov;success=$true;errorCode=$null;engineVersion=$engineVersion;oversizedItems=@()} | ConvertTo-Json -Compress -Depth 4
        } catch {
            $errCode = $_.Exception.GetType().Name
            @{schemaVersion=$ov;success=$false;errorCode=$errCode;engineVersion='';oversizedItems=@()} | ConvertTo-Json -Compress -Depth 4
        }
        """;
}

/// <summary>
/// Constrói o <see cref="ProcessStartInfo"/> da exportação: PowerShell NÃO INTERATIVO (<c>-NoProfile</c>,
/// <c>-NonInteractive</c>, <c>-NoLogo</c>), com o script INTERNO fixo (<see cref="EvExportProcessScript.Script"/>),
/// working directory controlado, PATH controlado e argumentos TIPADOS via variáveis de ambiente
/// (<c>EV_EXPORT_ARG_*</c>) — API segura equivalente a <c>ArgumentList</c>, nunca concatenação de string de
/// shell (item 7). Reutiliza EXATAMENTE o padrão já hardenado de
/// <c>EvPowerShellCommandBuilder</c> (Discovery).
/// </summary>
public static class EvExportProcessArgumentBuilder
{
    /// <summary>Prefixo das variáveis de ambiente que carregam os argumentos tipados.</summary>
    public const string ArgumentEnvironmentPrefix = "EV_EXPORT_ARG_";

    /// <summary>Monta o processo para a requisição de exportação, usando o executável PowerShell e o diretório físico de saída já resolvidos/contidos.</summary>
    public static ProcessStartInfo Build(
        EvExportProcessRequest request, string powerShellExecutable, string workingDirectory, string resolvedOutputDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(powerShellExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedOutputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // Flags e o SCRIPT FIXO adicionados via ArgumentList (API segura, nunca uma string concatenada):
        // nenhum valor de dado da requisição entra aqui.
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(EvExportProcessScript.Script);

        // Ambiente controlado: PATH restrito ao diretório do executável; todo dado da requisição entra
        // EXCLUSIVAMENTE aqui, como valor de variável de ambiente — nunca como argv/script.
        startInfo.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(powerShellExecutable)) ?? string.Empty;
        startInfo.Environment[ArgumentEnvironmentPrefix + "ArchiveId"] = request.Arguments.ExternalArchiveId;
        startInfo.Environment[ArgumentEnvironmentPrefix + "OutputDirectory"] = resolvedOutputDirectory;
        startInfo.Environment[ArgumentEnvironmentPrefix + "MaxThreads"] =
            request.Arguments.Policy.MaxThreads.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[ArgumentEnvironmentPrefix + "MaxPstSizeMb"] =
            request.Arguments.Policy.MaxPstSizeMb.ToString(CultureInfo.InvariantCulture);

        return startInfo;
    }
}
