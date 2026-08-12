# Vertical Slice 4A — Control Plane API e Portal Operacional

## Status

**Em desenvolvimento — PR deve permanecer em Draft até aprovação explícita do Decision Owner.**

## Objetivo

Entregar a primeira superfície operacional navegável do ArchiveBridge, instalada on-premises, conectada ao SQL Server existente e limitada às capacidades já implementadas nos Slices 1, 2 e 3.

O Slice 4A deve permitir visualizar, consultar e operar com controle de acesso:

- projetos de migração;
- ondas de migração;
- jobs duráveis, tentativas, retries, bloqueios e erros;
- ambientes Enterprise Vault cadastrados;
- execução da descoberta READ-ONLY de capacidades do Enterprise Vault;
- resultados `Ready`, `Blocked` e `Unsupported`;
- evidências técnicas e trilha de auditoria;
- upload e validação estrutural de CSV de mapeamento, sem iniciar exportação ou importação.

## Princípio de veracidade operacional

A interface não pode simular capacidades inexistentes.

Funcionalidades de slices futuros devem aparecer somente como indisponíveis, com motivo explícito e sem endpoint executável oculto. Exemplo:

```text
Exportar archive
Status: indisponível
Motivo: funcionalidade prevista para o Slice 4B
```

É proibido:

- executar mock como se fosse operação real;
- expor botão ativo para `Export-EVArchive`;
- produzir PST;
- iniciar AzCopy, Purview, Microsoft Graph ou qualquer ingestão no Microsoft 365;
- indicar laboratório EV validado quando `LaboratoryValidated = false`.

## Arquitetura do slice

```text
Navegador
   │ HTTPS
   ▼
ArchiveBridge Control Plane
   ├── Portal Web
   ├── API .NET
   ├── Autenticação e RBAC
   ├── Application/Domain existentes
   ├── Infrastructure existente
   └── SQL Server existente
```

### Restrições de implantação

- operação on-premises;
- hospedagem em IIS ou Windows Service/Kestrel conforme decisão de implantação;
- sem Azure App Service obrigatório;
- sem banco cloud obrigatório;
- sem SaaS da ArchiveBridge;
- sem comunicação externa, exceto integrações explicitamente configuradas e já autorizadas;
- nenhuma credencial ou segredo persistido em código, log, evidência ou configuração versionada.

## Escopo funcional

### 1. Autenticação e autorização

Implementar autenticação corporativa compatível com instalação on-premises e RBAC fail-closed.

Papéis mínimos previstos:

| Papel | Capacidades |
|---|---|
| Viewer | Consultar dashboard, projetos, ondas, jobs e evidências autorizadas |
| Operator | Executar operações permitidas, incluindo discovery READ-ONLY e retry autorizado |
| Approver | Aprovar ou rejeitar gates operacionais definidos pelo domínio |
| Administrator | Administrar configuração operacional e vínculos de acesso |
| Auditor | Consultar trilha e evidências, sem mutação operacional |

Toda autorização deve respeitar isolamento por tenant e projeto. Ausência de contexto, claim, vínculo ou permissão deve resultar em negação.

### 2. Dashboard operacional

Exibir dados reais persistidos, incluindo:

- projetos por estado;
- ondas por estado;
- jobs ativos, bloqueados, falhos e concluídos;
- tentativas e retries;
- ambientes EV por resultado de capacidade;
- pendências de aprovação;
- eventos operacionais recentes.

O dashboard não deve inventar KPIs quando não houver dados.

### 3. Projetos e ondas

Permitir consulta e navegação de:

- identificação do tenant e projeto;
- estado atual;
- gates e bloqueios;
- ondas vinculadas;
- jobs e evidências relacionados;
- histórico de transições.

Criação ou mutação somente quando já existir contrato de domínio suportado. O portal não deve introduzir atalhos que contornem invariantes existentes.

### 4. Upload e validação do CSV de mapeamento

O Slice 4A cobre somente recepção controlada e validação estrutural/semântica compatível com os contratos existentes.

Requisitos mínimos:

- limite de tamanho configurável e fail-closed;
- validação de extensão, encoding, cabeçalho, número de linhas e campos obrigatórios;
- prevenção de CSV injection na visualização e exportação;
- armazenamento de hash SHA-256 e metadados de custódia;
- relatório determinístico de erros por linha;
- nenhuma criação de import job no Purview;
- nenhuma chamada a Graph, Exchange Online, AzCopy ou Microsoft 365.

### 5. Jobs duráveis

Exibir e operar somente comandos já suportados:

- estado atual;
- número de tentativas;
- timestamps;
- erro estruturado e sanitizado;
- checkpoints disponíveis;
- retry quando permitido pelo domínio;
- bloqueio e motivo;
- correlação com projeto, onda e evidências.

Retry deve ser idempotente e exigir autorização. O portal não pode alterar diretamente tabelas para forçar transições.

### 6. Enterprise Vault Capability Discovery

Integrar o portal ao Slice 3, preservando todas as invariantes já aceitas:

- execução estritamente READ-ONLY;
- seleção por capacidades, não por versão textual;
- falha mecânica nunca resulta em `Ready`;
- resultado `Ready`, `Blocked` ou `Unsupported` sustentado por evidência;
- `LaboratoryValidated` permanece `false` até homologação real;
- nenhuma execução de `Export-EVArchive`;
- visualização de adapter selecionado, capacidades, achados, hashes e evidência;
- download autorizado do `evidence.json` sem alteração de bytes;
- verificação do hash antes da entrega do artefato.

### 7. Evidências e auditoria

Fornecer:

- consulta paginada e filtrável;
- download autorizado de artefatos;
- validação de tamanho e hash antes do streaming;
- correlação por tenant, projeto, execução, job e usuário;
- trilha append-only para acessos e mutações;
- sanitização de dados sensíveis em logs e mensagens de erro.

## Superfície de API inicial

A nomenclatura final deve seguir os padrões do repositório, mas a superfície funcional esperada inclui:

```text
GET  /health/live
GET  /health/ready
GET  /api/v1/dashboard
GET  /api/v1/projects
GET  /api/v1/projects/{projectId}
GET  /api/v1/projects/{projectId}/waves
GET  /api/v1/jobs
GET  /api/v1/jobs/{jobId}
POST /api/v1/jobs/{jobId}/retry
POST /api/v1/mappings/validate
GET  /api/v1/ev/environments
GET  /api/v1/ev/environments/{environmentId}/discoveries
POST /api/v1/ev/environments/{environmentId}/discoveries
GET  /api/v1/ev/discoveries/{discoveryId}
GET  /api/v1/ev/discoveries/{discoveryId}/evidence
GET  /api/v1/audit-events
```

Endpoints mutáveis devem exigir proteção contra CSRF quando aplicável, autenticação, autorização, idempotency key e validação de concorrência.

## Requisitos não funcionais

### Segurança

- HTTPS obrigatório fora de desenvolvimento local;
- headers de segurança;
- cookies seguros, `HttpOnly` e `SameSite` adequados quando usados;
- antiforgery nas operações baseadas em cookie;
- validação de entrada e limites de payload;
- output encoding;
- proteção contra path traversal e IDOR;
- rate limiting para operações sensíveis;
- correlação e auditoria sem registrar segredo;
- mensagens externas sem stack trace;
- consultas sempre filtradas por escopo autorizado.

### Concorrência e consistência

- optimistic concurrency onde houver mutação concorrente;
- idempotência para retry, discovery e upload;
- transações curtas;
- nenhuma transição de estado apenas na camada web;
- invariantes validadas antes da persistência.

### Observabilidade

- logs estruturados;
- correlation ID por requisição;
- métricas de latência, falhas e operações;
- health checks distintos para liveness e readiness;
- readiness não pode declarar prontidão quando banco ou dependências obrigatórias estiverem indisponíveis.

### Usabilidade

- interface responsiva para desktop operacional;
- navegação por teclado;
- contraste e labels acessíveis;
- paginação server-side;
- datas em UTC no contrato e apresentação localizada;
- estados e bloqueios acompanhados de explicação acionável.

## Critérios de aceite

1. O portal autentica o usuário e aplica RBAC fail-closed por tenant/projeto.
2. Dashboard, projetos, ondas e jobs exibem dados reais do SQL Server, sem mocks de produção.
3. Discovery EV pode ser iniciado por usuário autorizado e continua estritamente READ-ONLY.
4. O resultado da discovery e o `evidence.json` são exibidos/baixados com verificação de hash.
5. CSV é validado sem iniciar Purview, AzCopy, Graph ou importação.
6. Retry de job só ocorre quando permitido, é idempotente e auditado.
7. Funcionalidades de Slice 4B+ aparecem desabilitadas com motivo explícito e sem endpoint operacional.
8. Auditoria registra autenticação relevante, leitura de evidência, mutações, aprovações e retries.
9. Testes automatizados cobrem autorização, isolamento, IDOR, CSV injection, idempotência, concorrência, hash de evidência e estados indisponíveis.
10. `dotnet restore --locked-mode`, build Release, testes, format e secret scanning permanecem verdes.
11. Nenhuma migration aceita dos slices anteriores é alterada; novas migrations, se necessárias, são aditivas, determinísticas e protegidas pelos mecanismos do repositório.
12. O PR permanece Draft até a revisão formal do Decision Owner.

## Fora do escopo

- `Export-EVArchive`;
- seleção e exportação real de archives;
- geração, split, inspeção ou reparo de PST;
- Outlook automation;
- libpff;
- AzCopy;
- Azure staging;
- Purview;
- Microsoft Graph;
- Exchange Online;
- importação no Microsoft 365;
- reconciliação final;
- homologação contra Enterprise Vault real;
- declaração de compatibilidade produtiva universal.

## Estratégia de implementação incremental

1. Contratos do Control Plane, autenticação e isolamento.
2. Read models para dashboard, projetos, ondas e jobs.
3. Portal navegável e estados indisponíveis explícitos.
4. Integração com discovery EV do Slice 3 — **solicitação autorizada e durável** (ver abaixo).
5. Evidências, download verificado e auditoria.
6. Validação segura de CSV.
7. Retry autorizado e idempotente.
8. Hardening, testes de segurança, documentação e empacotamento on-premises.

### Stage 4 — Solicitação autorizada de descoberta EV (write-path)

O passo 4 é entregue como uma **solicitação assíncrona e durável**: o POST autenticado
(`EvDiscoveryOperators` = Operator/Administrator, protegido server-side; antiforgery ativo) resolve o escopo
a partir do principal, resolve site/directory/versão/hash/política **server-side** e enfileira um Job durável
idempotente via `RequestEvCapabilityDiscoveryUseCase`. O **processo web nunca executa a descoberta** — quem
executa é o Worker EV, mais tarde. Um feature gate local (`EnterpriseVaultDiscovery:Enabled`, default `false`)
controla se o Portal pode solicitar. Detalhes do contrato HTTP e do threat-model delta em
`docs/control-plane/slice-04a-control-plane-portal.md`.

## Regra de encerramento

Este slice não inicia o Slice 4B. Após aprovação e merge do Slice 4A, o desenvolvimento deve parar e aguardar definição formal da Exportação Controlada do Enterprise Vault.
