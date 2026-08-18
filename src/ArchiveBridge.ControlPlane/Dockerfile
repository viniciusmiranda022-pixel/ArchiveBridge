# syntax=docker/dockerfile:1
#
# ArchiveBridge Control Plane — imagem de DEMONSTRAÇÃO LOCAL (multi-stage).
# Build a partir do código-fonte (SDK) → publish → runtime enxuto (ASP.NET).
# Usada por docker-compose.demo.yml. O contexto de build é a RAIZ do repositório.
#
# Esta imagem é apenas para VER E NAVEGAR na interface em Presentation Mode. Ela não conecta a
# Enterprise Vault, Active Directory, Microsoft 365, Azure, Purview, Graph ou Exchange reais, e não executa
# PowerShell/Export-EVArchive/PST/AzCopy.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copia todo o código-fonte (o .dockerignore remove bin/obj/.git/.env*/artefatos de teste).
COPY . .

# A imagem oficial 'sdk:10.0' traz sempre o SDK mais recente do canal 10.0; o global.json do repositório
# fixa um patch específico para determinismo de CI. SÓ para esta imagem de DEMONSTRAÇÃO, regravamos o
# global.json dentro do build com roll-forward 'latestFeature' (aceita qualquer feature band de 10.0.x),
# para casar com o SDK da imagem base SEM alterar o arquivo versionado. O runtime alvo continua net10.0.
RUN printf '{ "sdk": { "version": "10.0.110", "rollForward": "latestFeature" } }\n' > global.json

# Restore + publish apenas do host do Control Plane (as bibliotecas referenciadas entram pelo ProjectReference).
RUN dotnet restore src/ArchiveBridge.ControlPlane/ArchiveBridge.ControlPlane.csproj
RUN dotnet publish src/ArchiveBridge.ControlPlane/ArchiveBridge.ControlPlane.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl é usado apenas pelo HEALTHCHECK do container (GET /health/ready).
RUN apt-get update \
        && apt-get install -y --no-install-recommends curl \
        && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# O Kestrel escuta na porta 8080 DENTRO do container; o host publica 8180 (ver compose).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ArchiveBridge.ControlPlane.dll"]
