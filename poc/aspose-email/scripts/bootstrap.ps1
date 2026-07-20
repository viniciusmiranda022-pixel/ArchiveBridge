<#
.SYNOPSIS
Cria o projeto DESCARTÁVEL da PoC na VM e adiciona o Aspose.Email — a única
etapa em que o pacote existe, e apenas fora do repositório do produto.

.EXAMPLE
.\bootstrap.ps1 -AsposeVersion 24.12.0 -LicensePath C:\poc\Aspose.Email.lic
#>
param(
    [Parameter(Mandatory)] [string] $AsposeVersion,
    [Parameter(Mandatory)] [string] $LicensePath,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\disposable')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Pré-checagens -----------------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw '.NET SDK não encontrado. Instale o .NET 10 SDK.' }
$sdkVersion = (& dotnet --version)
if (-not $sdkVersion.StartsWith('10.')) {
    Write-Warning "SDK detectado: $sdkVersion (plano pede .NET 10 LTS). Registre no relatório."
}
if (-not (Test-Path -LiteralPath $LicensePath)) {
    throw "Arquivo de licença não encontrado: $LicensePath (obtenha a licença de avaliação antes do bootstrap)."
}
# Segurança: nunca dentro da árvore git do produto com solution.
$repoRoot = Join-Path $PSScriptRoot '..\..\..'
if (Get-ChildItem -LiteralPath $repoRoot -Filter '*.slnx' -ErrorAction SilentlyContinue) {
    throw 'Solution do produto detectada na raiz. A PoC não pode coexistir com o scaffolding: use uma VM/clone dedicado.'
}

# --- Projeto descartável -----------------------------------------------------
$projDir = Join-Path $OutputDirectory 'AsposePoc'
New-Item -ItemType Directory -Path $projDir -Force | Out-Null
Push-Location $projDir
try {
    if (-not (Test-Path -LiteralPath (Join-Path $projDir 'AsposePoc.csproj'))) {
        & dotnet new console --name AsposePoc --output . --framework net10.0 | Out-Null
        Remove-Item -LiteralPath (Join-Path $projDir 'Program.cs') -ErrorAction SilentlyContinue
    }
    Copy-Item -Path (Join-Path $PSScriptRoot '..\src\*.cs') -Destination $projDir -Force

    # Única adição do pacote — na VM, nunca no repositório.
    & dotnet add package Aspose.Email --version $AsposeVersion
    if ($LASTEXITCODE -ne 0) { throw "dotnet add package falhou ($LASTEXITCODE)." }

    & dotnet build -c Release
    if ($LASTEXITCODE -ne 0) {
        throw 'Build falhou. Ajustes de superfície de API são esperados (fontes escritas sem Aspose disponível) — corrija, registre o diff no relatório e reexecute.'
    }
}
finally { Pop-Location }

# Licença via variável de ambiente lida pelo harness; caminho fora do repo.
[Environment]::SetEnvironmentVariable('ASPOSE_POC_LICENSE', $LicensePath, 'Process')
Write-Host ''
Write-Host "Bootstrap concluído. Binário: $projDir\bin\Release\net10.0\AsposePoc.exe"
Write-Host 'Defina ASPOSE_POC_LICENSE no processo que executar o harness.'
Write-Host 'Próximo passo: gerar corpus em escala smoke (ver README).'
