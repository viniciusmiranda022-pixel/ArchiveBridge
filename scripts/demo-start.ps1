#Requires -Version 5.1
<#
.SYNOPSIS
    Sobe o ambiente de DEMONSTRAÇÃO LOCAL do ArchiveBridge (Docker Desktop).

.DESCRIPTION
    Executa verificações seguras e então sobe o docker-compose.demo.yml:
      1. Docker está rodando?
      2. Existe .env.demo (com os segredos locais)?
      3. A porta 8180 do host está livre? (app)
      4. A porta 14335 do host está livre? (SQL, só troubleshooting)
      5. docker compose up -d --build
      6. Aguarda o container da app ficar 'healthy'
      7. Imprime a URL da demo

    Este script NÃO encerra processos e NÃO mexe em portas. Se 8180 ou 14335 estiverem
    ocupadas, ele apenas avisa e PARA — cabe a você liberar a porta. As portas 8080 e 8090
    NÃO são usadas por esta demo e nunca são tocadas.

    A demo roda em Presentation Mode com dados 100% sintéticos e não conecta a nenhum
    sistema real (Enterprise Vault / Active Directory / Microsoft 365 / Azure / Purview /
    Graph / Exchange).

.EXAMPLE
    ./scripts/demo-start.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Raiz do repositório (o script vive em scripts/).
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $RepoRoot 'docker-compose.demo.yml'
$EnvFile     = Join-Path $RepoRoot '.env.demo'
$AppPort     = 8180
$SqlPort     = 14335
$DemoUrl     = 'http://localhost:8180'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Fail($msg) {
    Write-Host "    ERRO  $msg" -ForegroundColor Red
    exit 1
}

# Testa se uma porta TCP do host já está em uso, sem depender de módulos opcionais.
function Test-PortInUse([int]$Port) {
    try {
        $conns = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop
        return ($null -ne $conns)
    }
    catch {
        # Get-NetTCPConnection lança quando não há nenhuma conexão na porta => porta livre.
        return $false
    }
}

# 1. Docker rodando?
Write-Step 'Verificando o Docker'
try {
    docker info --format '{{.ServerVersion}}' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker info falhou' }
    Write-Ok 'Docker está em execução'
}
catch {
    Fail 'Docker não está em execução. Abra o Docker Desktop e tente novamente.'
}

# 2. .env.demo existe?
Write-Step 'Verificando o arquivo de segredos .env.demo'
if (-not (Test-Path -LiteralPath $EnvFile)) {
    Write-Host ''
    Write-Host '    .env.demo não encontrado.' -ForegroundColor Yellow
    Write-Host '    Copie o modelo e defina senhas fortes antes de subir a demo:' -ForegroundColor Yellow
    Write-Host '        copy .env.demo.example .env.demo' -ForegroundColor Yellow
    Write-Host '    (edite .env.demo e troque os dois CHANGE_ME_STRONG_PASSWORD)' -ForegroundColor Yellow
    Fail 'Sem .env.demo não há como subir a demo.'
}
Write-Ok '.env.demo encontrado'

# 3. Porta 8180 livre?
Write-Step "Verificando a porta $AppPort (aplicação)"
if (Test-PortInUse $AppPort) {
    Fail "Porta $AppPort já está em uso. Libere-a (ou pare o outro serviço) e rode novamente. Este script não encerra processos."
}
Write-Ok "Porta $AppPort livre"

# 4. Porta 14335 livre?
Write-Step "Verificando a porta $SqlPort (SQL)"
if (Test-PortInUse $SqlPort) {
    Fail "Porta $SqlPort já está em uso. Libere-a (ou pare o outro serviço) e rode novamente. Este script não encerra processos."
}
Write-Ok "Porta $SqlPort livre"

# 5. Sobe a demo.
Write-Step 'Subindo os serviços (docker compose up -d --build)'
Push-Location $RepoRoot
try {
    docker compose --env-file $EnvFile -f $ComposeFile up -d --build
    if ($LASTEXITCODE -ne 0) { Fail 'docker compose up falhou. Veja a saída acima.' }
}
finally {
    Pop-Location
}

# 6. Aguarda o container da app ficar 'healthy'.
Write-Step 'Aguardando a aplicação ficar saudável (isso pode levar 1-2 minutos no primeiro build)'
$deadline = (Get-Date).AddMinutes(5)
$healthy  = $false
while ((Get-Date) -lt $deadline) {
    $state = (docker inspect --format '{{.State.Health.Status}}' archivebridge-demo 2>$null)
    if ($state -eq 'healthy') { $healthy = $true; break }
    if ($state -eq 'unhealthy') {
        Write-Host '    Container reportou unhealthy; últimas linhas do log:' -ForegroundColor Yellow
        docker logs --tail 30 archivebridge-demo
        Fail 'A aplicação não ficou saudável. Verifique os logs acima.'
    }
    Start-Sleep -Seconds 5
}

if (-not $healthy) {
    Write-Host '    Tempo esgotado esperando health. Últimas linhas do log:' -ForegroundColor Yellow
    docker logs --tail 30 archivebridge-demo
    Fail 'A aplicação não ficou saudável dentro do tempo limite.'
}

# 7. Pronto.
Write-Host ''
Write-Ok 'ArchiveBridge Demo disponível: ' -NoNewline
Write-Host $DemoUrl -ForegroundColor Green
Write-Host ''
Write-Host '    Login:   admin.demo' -ForegroundColor Gray
Write-Host '    Senha:   a que você definiu em .env.demo (ARCHIVEBRIDGE_DEMO_ADMIN_PASSWORD)' -ForegroundColor Gray
Write-Host '    Modo:    Presentation Mode — dados sintéticos, nenhuma operação real.' -ForegroundColor Gray
Write-Host ''
Write-Host '    Para parar:   ./scripts/demo-stop.ps1' -ForegroundColor Gray
Write-Host '    Para zerar:   ./scripts/demo-reset.ps1   (APAGA o banco da demo)' -ForegroundColor Gray
