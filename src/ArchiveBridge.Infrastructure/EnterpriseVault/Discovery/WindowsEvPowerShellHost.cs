using System.Diagnostics;
using System.Text;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Catálogo IMUTÁVEL e versionado dos scripts internos de sonda (um por <see cref="EvPowerShellProbeKind"/>).
/// Usam apenas comandos documentados de leitura (<c>Get-Command</c>, <c>Get-EVSite</c>, <c>Get-EVServer</c>,
/// <c>Get-EVVaultStore</c>, <c>Get-EVArchive</c>, <c>Test-Path</c>) e emitem o envelope JSON versionado.
/// NENHUM script executa <c>Export-EVArchive</c> — no máximo descobre seus metadados. Os argumentos são
/// lidos de variáveis de ambiente controladas (<c>EV_PROBE_ARG_*</c>), nunca da linha de comando.
/// </summary>
public static class EvProbeScriptCatalog
{
    /// <summary>Versão do catálogo (parte do <c>ConfigurationHash</c>).</summary>
    public const int Version = EvDiscoverySchema.ProbeCatalogVersion;

    private const string Header = "$ErrorActionPreference='Stop'; $ov=1;";

    private static readonly Dictionary<EvPowerShellProbeKind, string> Scripts =
        new()
        {
            [EvPowerShellProbeKind.PowerShellEnvironment] =
                Header + "@{schemaVersion=$ov;probe='PowerShellEnvironment';success=$true;errorCode=$null;data=@{psVersion=\"$($PSVersionTable.PSVersion)\"}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.AvailableEvModule] =
                Header + "$m=Get-Module -ListAvailable -Name 'Symantec.EnterpriseVault*' -ErrorAction SilentlyContinue;@{schemaVersion=$ov;probe='AvailableEvModule';success=$true;errorCode=$null;data=@{present=[bool]$m}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.RegisteredEvSnapin] =
                Header + "$s=Get-PSSnapin -Registered -Name 'Symantec.EnterpriseVault*' -ErrorAction SilentlyContinue;@{schemaVersion=$ov;probe='RegisteredEvSnapin';success=$true;errorCode=$null;data=@{present=[bool]$s}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.EvSite] =
                Header + "$c=@(Get-EVSite).Count;@{schemaVersion=$ov;probe='EvSite';success=$true;errorCode=$null;data=@{count=$c}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.EvServer] =
                Header + "$c=@(Get-EVServer).Count;@{schemaVersion=$ov;probe='EvServer';success=$true;errorCode=$null;data=@{count=$c}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.EvVaultStore] =
                Header + "$c=@(Get-EVVaultStore).Count;@{schemaVersion=$ov;probe='EvVaultStore';success=$true;errorCode=$null;data=@{count=$c}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.EvArchive] =
                Header + "$c=@(Get-EVArchive).Count;@{schemaVersion=$ov;probe='EvArchive';success=$true;errorCode=$null;data=@{count=$c}}|ConvertTo-Json -Compress",
            [EvPowerShellProbeKind.ExportEvArchiveCommandMetadata] =
                Header + "$cmd=Get-Command -Name 'Export-EVArchive' -ErrorAction SilentlyContinue;if($null -eq $cmd){$d=@{available=$false}}else{$d=@{available=$true;observedVersion=\"$($cmd.Version)\";productVersion=\"$($cmd.Version)\";signature=@{commandName=$cmd.Name;moduleSource=\"$($cmd.Source)\";version=\"$($cmd.Version)\";commandType=\"$($cmd.CommandType)\";parameters=@($cmd.Parameters.Keys);mandatory=@($cmd.Parameters.Values|Where-Object{$_.Attributes.Mandatory -contains $true}|ForEach-Object{$_.Name});parameterSets=@($cmd.ParameterSets|ForEach-Object{$_.Name})}}};@{schemaVersion=$ov;probe='ExportEvArchiveCommandMetadata';success=$true;errorCode=$null;data=$d}|ConvertTo-Json -Depth 6 -Compress",
            [EvPowerShellProbeKind.StagingPathAccess] =
                Header + "$p=$env:EV_PROBE_ARG_StagingPath;$ok=if([string]::IsNullOrWhiteSpace($p)){$false}else{Test-Path -LiteralPath $p};@{schemaVersion=$ov;probe='StagingPathAccess';success=$true;errorCode=$null;data=@{accessible=[bool]$ok}}|ConvertTo-Json -Compress",
        };

    /// <summary>Script interno FIXO da sonda; lança para uma operação desconhecida (fail-closed).</summary>
    public static string ScriptFor(EvPowerShellProbeKind kind) =>
        Scripts.TryGetValue(kind, out var script)
            ? script
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Operação de sonda desconhecida.");
}

/// <summary>
/// Constrói o <see cref="ProcessStartInfo"/> de uma sonda: PowerShell NÃO INTERATIVO (<c>-NoProfile</c>,
/// <c>-NonInteractive</c>, <c>-NoLogo</c>), com o script INTERNO fixo, working directory controlado, PATH
/// controlado e ARGUMENTOS TIPADOS via variáveis de ambiente (<c>EV_PROBE_ARG_*</c>) — nunca na linha de
/// comando, nunca concatenados no script, sem credencial. Portável e testável isoladamente.
/// </summary>
public static class EvPowerShellCommandBuilder
{
    /// <summary>Prefixo das variáveis de ambiente que carregam os argumentos tipados.</summary>
    public const string ArgumentEnvironmentPrefix = "EV_PROBE_ARG_";

    /// <summary>Monta o processo para a requisição, usando o executável PowerShell e o working directory dados.</summary>
    public static ProcessStartInfo Build(EvProbeRequest request, string powerShellExecutable, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(powerShellExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var script = EvProbeScriptCatalog.ScriptFor(request.Kind);

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
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        // Ambiente controlado: PATH restrito ao diretório do executável; argumentos tipados via env.
        startInfo.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(powerShellExecutable)) ?? string.Empty;
        foreach (var argument in request.Arguments)
        {
            startInfo.Environment[ArgumentEnvironmentPrefix + argument.Name] = argument.Value;
        }

        return startInfo;
    }
}

/// <summary>
/// Host PowerShell para Windows Worker: mapeia a sonda tipada para o script interno fixo, monta o processo
/// controlado e o executa com captura byte-limitada. A prova contra Enterprise Vault REAL é pendente de
/// laboratório; fora do Windows, <see cref="RunAsync"/> lança <see cref="PlatformNotSupportedException"/>
/// (o construtor de comando e o runner byte-limitado permanecem testáveis de forma portável).
/// </summary>
public sealed class WindowsEvPowerShellHost(string powerShellExecutable, string workingDirectory) : IEvPowerShellHost
{
    private readonly string _powerShellExecutable = powerShellExecutable;
    private readonly string _workingDirectory = workingDirectory;

    /// <inheritdoc />
    public async Task<EvProbeExecution> RunAsync(EvProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "O host PowerShell do Enterprise Vault requer Windows Worker; a prova contra EV real é pendente de laboratório.");
        }

        var startInfo = EvPowerShellCommandBuilder.Build(request, _powerShellExecutable, _workingDirectory);
        var run = await ByteLimitedProcessRunner.RunAsync(startInfo, request.Timeout, request.MaxOutputBytes, cancellationToken).ConfigureAwait(false);
        return new EvProbeExecution(run.ExitCode, run.StandardOutput, run.StandardError, run.TimedOut, run.OutputLimitExceeded);
    }
}
