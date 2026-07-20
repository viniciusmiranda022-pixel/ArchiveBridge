<#
.SYNOPSIS
Coletor EXTERNO de métricas: amostra memória e handles de um processo em
CSV até ele encerrar. Independente das métricas internas do harness — o
avaliador cruza as duas fontes.

.EXAMPLE
.\collect-metrics.ps1 -ProcessId 1234 -OutCsv D:\poc\metrics\ct2-500gb.csv
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $OutCsv,
    [int] $IntervalSeconds = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

New-Item -ItemType Directory -Path (Split-Path -Parent $OutCsv) -Force | Out-Null
'utc,workingSetMb,privateMb,handles' | Set-Content -LiteralPath $OutCsv -Encoding UTF8

while ($true) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { break }
    try {
        $line = '{0},{1},{2},{3}' -f `
            (Get-Date).ToUniversalTime().ToString('o'), `
            [math]::Round($p.WorkingSet64 / 1MB, 1), `
            [math]::Round($p.PrivateMemorySize64 / 1MB, 1), `
            $p.HandleCount
        Add-Content -LiteralPath $OutCsv -Value $line -Encoding UTF8
    } catch {
        # Processo pode encerrar entre o Get-Process e a leitura.
        break
    }
    Start-Sleep -Seconds $IntervalSeconds
}
Write-Host "Coleta encerrada: $OutCsv"
