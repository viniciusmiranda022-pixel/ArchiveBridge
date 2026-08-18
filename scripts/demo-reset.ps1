#Requires -Version 5.1
<#
.SYNOPSIS
    Zera o ambiente de DEMONSTRAÇÃO LOCAL do ArchiveBridge (APAGA o banco da demo).

.DESCRIPTION
    Executa 'docker compose down -v': remove os containers, a rede e o VOLUME de dados
    (archivebridge-demo-sqldata). O banco ArchiveBridgeDemo é DESTRUÍDO.

    Como a demo recria o schema (migrations no startup) e o administrador de bootstrap
    a cada subida, esse reset é a forma de voltar a um estado limpo. Na próxima vez que
    você rodar ./scripts/demo-start.ps1, tudo é reconstruído do zero.

    Nada de real é afetado: os dados são 100% sintéticos e locais.

.EXAMPLE
    ./scripts/demo-reset.ps1
#>
[CmdletBinding()]
param(
    # Pula a confirmação interativa (use com cuidado em automações).
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $RepoRoot 'docker-compose.demo.yml'
$EnvFile     = Join-Path $RepoRoot '.env.demo'

Write-Host '==> RESET da demo' -ForegroundColor Yellow
Write-Host '    Isto vai APAGAR o banco da demonstração (volume archivebridge-demo-sqldata).' -ForegroundColor Yellow
Write-Host '    Os dados são sintéticos e serão recriados no próximo start.' -ForegroundColor Yellow

if (-not $Force) {
    $answer = Read-Host '    Digite "sim" para confirmar'
    if ($answer -ne 'sim') {
        Write-Host '    Cancelado. Nada foi apagado.' -ForegroundColor Gray
        exit 0
    }
}

Push-Location $RepoRoot
try {
    # --env-file evita o aviso de interpolação; 'down -v' remove containers, rede E o volume de dados.
    docker compose --env-file $EnvFile -f $ComposeFile down -v
    if ($LASTEXITCODE -ne 0) {
        Write-Host '    ERRO  docker compose down -v falhou. Veja a saída acima.' -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

Write-Host '    OK  Demo removida e banco apagado.' -ForegroundColor Green
Write-Host '    Para recriar do zero: ./scripts/demo-start.ps1' -ForegroundColor Gray
