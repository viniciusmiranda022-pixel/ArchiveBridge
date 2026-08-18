# ArchiveBridge — Ambiente local de demonstração via Docker

Este documento empacota a interface do ArchiveBridge (o *Client Demo* descrito em
[`docs/ui/README.md`](README.md)) em um **ambiente local de um comando**, para você abrir no navegador e
**ver e navegar** a plataforma em **Presentation Mode**.

> **É só uma vitrine.** O ambiente sobe em **Presentation Mode** com **dados 100% sintéticos**. Ele **não**
> conecta a nada real — Enterprise Vault, Active Directory, Microsoft 365, Azure, Purview, Graph ou Exchange —
> e **não** executa `Export-EVArchive`, geração de PST nem `AzCopy`. **Nenhuma operação real é executada.**

---

## O que sobe

Orquestrados por [`docker-compose.demo.yml`](../../docker-compose.demo.yml):

| Serviço | Container | Imagem | Papel |
|---|---|---|---|
| `archivebridge-demo` | `archivebridge-demo` | build local (`src/ArchiveBridge.ControlPlane/Dockerfile`) | O Control Plane (ASP.NET Core) em Presentation Mode |
| `sqlserver-demo` | `archivebridge-sql-demo` | `mcr.microsoft.com/mssql/server:2022-latest` | Banco `ArchiveBridgeDemo`, exclusivo da demo |
| `sqlserver-demo-init` | `archivebridge-sql-demo-init` | `mcr.microsoft.com/mssql/server:2022-latest` | Passo único que cria o banco vazio quando o SQL fica saudável; roda e sai |

- Rede dedicada: `archivebridge-demo-network`. Volume de dados: `archivebridge-demo-sqldata`.
- Ordem garantida: o SQL sobe → o passo de init cria o banco `ArchiveBridgeDemo` → só então a aplicação
  inicia (via `depends_on` com `service_healthy` e `service_completed_successfully`).
- O schema é **criado e migrado automaticamente** pelas migrations no startup da aplicação — sem instalar
  SQL, sem rodar migrations à mão, sem editar connection string. O administrador de bootstrap (`admin.demo`)
  também é criado sozinho. O container `sqlserver-demo-init` aparece como `Exited (0)` após concluir — isso é
  o esperado.

### Portas

| Onde | Host (sua máquina) | Container | Observação |
|---|---|---|---|
| Aplicação | **8180** | 8080 | Abra `http://localhost:8180` |
| SQL Server | **14335** | 1433 | Publicada **só para troubleshooting** com um cliente SQL; a app usa o hostname interno `sqlserver-demo` |

> As portas **8080** e **8090** do host **não são usadas** por esta demo. Entre os containers, a app fala com
> o banco por `sqlserver-demo,1433` — **nunca** `localhost:14335`.

---

## Pré-requisitos

- **Docker Desktop** em execução (Windows/macOS/Linux).
- Portas de host **8180** e **14335** livres.

---

## Passo a passo (primeira vez)

Todos os comandos partem da **raiz do repositório**.

### 1. Crie o arquivo de segredos `.env.demo`

Os segredos vêm **exclusivamente** de um `.env.demo` local, **não versionado**. Copie o modelo e defina
senhas fortes:

```powershell
# Windows (PowerShell)
copy .env.demo.example .env.demo
```

```bash
# macOS / Linux
cp .env.demo.example .env.demo
```

Edite o `.env.demo` e troque os dois `CHANGE_ME_STRONG_PASSWORD`:

```dotenv
ARCHIVEBRIDGE_DEMO_SQL_PASSWORD=UmaSenhaForte!2026#Sql
ARCHIVEBRIDGE_DEMO_ADMIN_PASSWORD=OutraSenhaForte!2026#Admin
```

> A senha do SQL precisa atender à complexidade do SQL Server (mín. 8 caracteres, com pelo menos três entre
> maiúscula, minúscula, dígito e símbolo). São senhas descartáveis de um ambiente sintético — não reutilize
> senhas de produção.

### 2. Suba o ambiente

**Windows (recomendado):** o script faz as verificações de segurança (Docker ativo, `.env.demo` presente,
portas 8180/14335 livres), sobe os serviços e espera ficar saudável:

```powershell
./scripts/demo-start.ps1
```

**Qualquer sistema (comando direto):**

```bash
docker compose --env-file .env.demo -f docker-compose.demo.yml up -d --build
```

O primeiro `up` compila a imagem e sobe o SQL antes da app (a app só inicia quando o banco está *healthy*),
então pode levar 1–2 minutos. Nas próximas vezes é quase instantâneo.

### 3. Abra no navegador

```
http://localhost:8180
```

Faça login:

| Campo | Valor |
|---|---|
| Usuário | `admin.demo` |
| Senha | a que você definiu em `ARCHIVEBRIDGE_DEMO_ADMIN_PASSWORD` |

Você cai no ArchiveBridge em **Presentation Mode**, com o banner de demonstração no topo e todos os dados
sintéticos (dataset *Contoso Demo*).

---

## Operação do dia a dia

Os comandos abaixo usam o arquivo compose diretamente. No Windows, os scripts em `scripts/` embrulham os mais
comuns.

### Ver o estado dos serviços

```bash
docker compose --env-file .env.demo -f docker-compose.demo.yml ps
```

### Ver os logs

```bash
# Segue o log da aplicação
docker compose --env-file .env.demo -f docker-compose.demo.yml logs -f archivebridge-demo

# Logs do banco
docker compose --env-file .env.demo -f docker-compose.demo.yml logs -f sqlserver-demo
```

### Parar (preservando o banco)

```powershell
./scripts/demo-stop.ps1
```

```bash
docker compose --env-file .env.demo -f docker-compose.demo.yml stop
```

Encerra os containers **mantendo** o volume de dados. Bom para desligar no fim do dia.

### Religar de onde parou

```powershell
./scripts/demo-start.ps1
```

```bash
docker compose --env-file .env.demo -f docker-compose.demo.yml start
```

### Derrubar (remove containers e rede; **preserva** o volume)

```bash
docker compose --env-file .env.demo -f docker-compose.demo.yml down
```

### Zerar tudo (remove containers, rede **e o banco**)

```powershell
./scripts/demo-reset.ps1
```

```bash
docker compose --env-file .env.demo -f docker-compose.demo.yml down -v
```

> ⚠️ **`down -v` APAGA o banco da demo** (o volume `archivebridge-demo-sqldata`). Como o schema e o
> administrador de bootstrap são recriados no próximo `up`/`start`, é exatamente assim que você volta a um
> estado limpo — mas qualquer navegação anterior é descartada. Não há nada de real para perder: os dados são
> sintéticos.

---

## Tabela de referência rápida

| Objetivo | Script (Windows) | Comando direto |
|---|---|---|
| Subir / religar | `./scripts/demo-start.ps1` | `docker compose --env-file .env.demo -f docker-compose.demo.yml up -d --build` |
| Ver status | — | `docker compose --env-file .env.demo -f docker-compose.demo.yml ps` |
| Ver logs da app | — | `docker compose --env-file .env.demo -f docker-compose.demo.yml logs -f archivebridge-demo` |
| Parar (preserva dados) | `./scripts/demo-stop.ps1` | `docker compose --env-file .env.demo -f docker-compose.demo.yml stop` |
| Derrubar (preserva volume) | — | `docker compose --env-file .env.demo -f docker-compose.demo.yml down` |
| Zerar (apaga o banco) | `./scripts/demo-reset.ps1` | `docker compose --env-file .env.demo -f docker-compose.demo.yml down -v` |

---

## Solução de problemas

- **`Porta 8180 já está em uso.` / `Porta 14335 já está em uso.`** — outro processo está ocupando a porta de
  host. Os scripts **não** encerram processos nem mexem em portas: libere a porta manualmente (ou pare o outro
  serviço) e rode de novo. As portas **8080** e **8090** não têm relação com esta demo e não são tocadas.
- **A app fica `unhealthy` ou reiniciando** — quase sempre o `.env.demo` está ausente ou com uma senha de SQL
  fraca demais para a política do SQL Server. Confira o `.env.demo`, rode `./scripts/demo-reset.ps1` e suba de
  novo. Os logs ajudam: `docker compose --env-file .env.demo -f docker-compose.demo.yml logs archivebridge-demo`.
- **Quero começar do zero** — `./scripts/demo-reset.ps1` (ou `docker compose --env-file .env.demo -f docker-compose.demo.yml down -v`)
  e depois `./scripts/demo-start.ps1`.

---

## Garantias de segurança da demo

- **Presentation Mode** ativo (`PresentationMode__Enabled=true`) sob `ASPNETCORE_ENVIRONMENT=Development`. O
  provedor de dados é sintético e **somente leitura**; nenhuma operação de negócio é executada.
- **Nenhuma conexão real**: sem Enterprise Vault, Active Directory, Microsoft 365, Azure, Purview, Graph ou
  Exchange. Sem `Export-EVArchive`, sem PST, sem `AzCopy`.
- **Segredos fora do versionamento**: as senhas vivem só no seu `.env.demo` local (ignorado pelo Git). O
  repositório versiona apenas o modelo `.env.demo.example`, com placeholders.
- **Banco isolado**: `ArchiveBridgeDemo`, em um volume próprio, sem relação com qualquer base real.
