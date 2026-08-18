#Requires -Version 5.1
<#
.SYNOPSIS
    Para o ambiente de DEMONSTRAÇÃO LOCAL do ArchiveBridge, PRESERVANDO os dados.

.DESCRIPTION
    Executa 'docker compose stop': encerra os containers da app e do SQL, mas mantém
    o volume com o banco ArchiveBridgeDemo. Para religar de onde parou, use
    ./scripts/demo-start.ps1 (ou 'docker compose ... start').

    Este comando NÃO apaga o banco. Para zerar tudo, use ./scripts/demo-reset.ps1.

.EXAMPLE
    ./scripts/demo-stop.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $RepoRoot 'docker-compose.demo.yml'
$EnvFile     = Join-Path $RepoRoot '.env.demo'

Write-Host '==> Parando os serviços da demo (os dados são preservados)' -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    # --env-file evita o aviso de interpolação; 'stop' apenas encerra os containers, sem apagar dados.
    docker compose --env-file $EnvFile -f $ComposeFile stop
    if ($LASTEXITCODE -ne 0) {
        Write-Host '    ERRO  docker compose stop falhou. Veja a saída acima.' -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

Write-Host '    OK  Demo parada. O banco ArchiveBridgeDemo foi preservado.' -ForegroundColor Green
Write-Host '    Para religar: ./scripts/demo-start.ps1' -ForegroundColor Gray
