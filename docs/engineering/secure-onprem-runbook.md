# Runbook de Engenharia Segura e Arquitetura On-Premises — ArchiveBridge

**Versão:** 1.0 — diretriz proposta ao desenvolvedor  
**Data:** 23 de julho de 2026  
**Escopo:** ArchiveBridge — migração Enterprise Vault/PST para Microsoft 365 / Exchange Online Archive  
**Classificação:** Confidencial — engenharia e segurança  
**Decision Owner:** Vinicius Miranda  

---

## 0. Regra de precedência e estado atual

Este documento consolida **como o desenvolvedor deve projetar, implementar, testar, revisar e entregar código** do ArchiveBridge. Ele não substitui os ADRs aceitos; ele os transforma em regras operacionais de desenvolvimento.

### 0.1 Ordem de precedência

Em caso de divergência, aplicar esta ordem:

1. ADR aceito mais recente;
2. evidência vinculada ao ADR;
3. este runbook;
4. runbook de engenharia v1.0;
5. documentação auxiliar;
6. preferência pessoal do desenvolvedor ou framework.

Nenhuma preferência de biblioteca, padrão ou ferramenta pode contrariar um ADR aceito.

### 0.2 Estado das decisões e bloqueio atual

**Estado das decisões (28/07/2026 — Decision Owner Vinicius Miranda):**

- **ADR-0006 — aceito (arquitetural).** Purview Network Upload é o adapter GA inicial; a **validação operacional em tenant (Gate A) permanece obrigatória antes de produção/certificação**.
- **ADR-0008 — aceito arquiteturalmente com condições.** A **assinatura institucional de Segurança/DPO é requisito anterior à produção** (não bloqueia o início do desenvolvimento). **Perfil HA de segredos: `BLOCKED_PENDING_EVIDENCE`.**
- **ADR-0005 — aceito como decisão de não inclusão no MVP.** O **libpff fica fora do MVP** como capacidade opcional futura (`LibpffIndependentValidation = BLOCKED_PENDING_EVIDENCE`, **não bloqueadora**); **o MVP não distribui** libpff / `pffinfo.exe` / `pffexport.exe` / bibliotecas LGPL relacionadas.

**Ainda não criar scaffolding ou código de produto.** As três aceitações acima estão registradas em **PRs documentais ainda não mergeados**; o **início do desenvolvimento só será liberado depois dos merges documentais em `main` e da confirmação final do Decision Owner**.

Até essa liberação, o desenvolvedor pode trabalhar somente em:

- ADRs;
- evidências;
- threat models;
- contratos documentais;
- matrizes de capacidades;
- planos de teste;
- runbooks operacionais;
- validações documentais e laboratoriais autorizadas.

É proibido antecipar scaffolding “para ganhar tempo”.

---

# 1. Baseline arquitetural obrigatória

## 1.1 Produto on-premises

O ArchiveBridge é instalado e operado na infraestrutura do cliente.

Baseline:

- Control Plane local;
- SQL Server local;
- fila durável no SQL Server;
- workers como Windows Services isolados;
- storage local, NTFS, NAS ou SMB;
- logs, backups, segredos e evidências sob controle do cliente;
- nenhuma dependência obrigatória de Azure SQL, Service Bus, Blob, Key Vault ou outro PaaS;
- nenhuma Control Plane SaaS;
- nenhuma porta de entrada publicada na internet;
- Microsoft 365 é um **destino externo**, acessado somente pelos adapters autorizados.

Azure ou brokers poderão existir no futuro apenas como adapters opcionais, mediante ADR e evidência. Nunca devem contaminar o domínio.

## 1.2 Estilo arquitetural

A arquitetura inicial é:

- **monólito modular** no plano de controle;
- **workers isolados** para processamento pesado e integrações;
- **arquitetura hexagonal**;
- **Domain-Driven Design pragmático**;
- execução assíncrona com estado durável;
- at-least-once técnico e exactly-once de efeito;
- fail closed;
- capability gates;
- artefatos imutáveis;
- operação humana explícita quando o serviço de destino exigir portal.

## 1.3 Microsserviços não são a arquitetura inicial

Não dividir o plano de controle em microsserviços.

Microsserviços só poderão ser considerados quando existirem simultaneamente:

1. gargalo ou blast radius comprovado por medição;
2. fronteira de domínio estável;
3. necessidade real de escala ou implantação independente;
4. capacidade operacional para observabilidade, deploy, segurança e recuperação distribuída;
5. ADR aprovado.

Antes disso, usar módulos internos com fronteiras verificadas por testes de arquitetura.

---

# 2. Princípios de implementação

## 2.1 KISS — simplicidade como regra

Implementar a solução mais simples que preserve:

- regra de negócio;
- segurança;
- idempotência;
- auditabilidade;
- recuperação;
- desempenho requerido;
- isolamento.

Simplicidade não significa remover controles críticos.

### Proibido

- metaprogramação desnecessária;
- frameworks internos genéricos antes de existir necessidade;
- reflection para substituir contratos claros;
- pipelines dinâmicos difíceis de depurar;
- abstrações com uma única finalidade hipotética;
- “engine universal” para todos os adapters;
- DSL própria sem justificativa;
- código que economiza linhas, mas esconde estado ou fluxo.

### Obrigatório

- nomes explícitos;
- fluxos legíveis;
- estados enumerados;
- contratos pequenos;
- validação próxima da entrada;
- erros por código estável;
- decisões registradas.

## 2.2 DRY sem abstração prematura

Duplicação pequena e explícita é preferível a uma abstração errada.

Aplicar a **regra de três**:

- primeira ocorrência: implementar claramente;
- segunda ocorrência: observar sem generalizar automaticamente;
- terceira ocorrência equivalente: avaliar extração.

Extrair apenas quando a duplicação representar o **mesmo conceito de domínio**, não apenas código visualmente parecido.

### Exemplo

Não criar um `UniversalProviderAdapter<T>` para EV, Purview e Graph. Esses provedores possuem contratos, riscos e estados diferentes.

Pode existir um contrato comum pequeno, como:

```csharp
public interface ITargetIngestor
{
    Task<CapabilityAssessment> AssessAsync(
        TargetContext context,
        CancellationToken cancellationToken);

    Task<SubmissionResult> SubmitAsync(
        ApprovedArtifact artifact,
        TargetContext context,
        CancellationToken cancellationToken);

    Task<ReconciliationResult> ReconcileAsync(
        ProviderOperationId operationId,
        CancellationToken cancellationToken);
}
```

Cada adapter implementa o contrato sem compartilhar lógica de fornecedor no domínio.

## 2.3 SOLID aplicado de forma concreta

### Single Responsibility

Uma classe deve ter um motivo de mudança.

Exemplos:

- `PurviewMappingCsvBuilder`: gerar CSV;
- `PurviewMappingCsvValidator`: validar schema e regras;
- `AzCopyProcessRunner`: executar processo;
- `PurviewSubmissionCoordinator`: coordenar operação;
- `ExternalOperationLedger`: persistir intenção e resultado.

Não criar `PurviewService` com 2.000 linhas fazendo tudo.

### Open/Closed

Adicionar novo adapter por nova implementação de porta, não por `if/else` espalhado.

### Liskov

Implementações devem respeitar o contrato completo. Um adapter que retorna `Success` sem reconciliação viola o contrato.

### Interface Segregation

Interfaces pequenas por capacidade. Não criar interface com métodos que vários adapters retornam `NotSupportedException`.

### Dependency Inversion

Domain e Application dependem de abstrações. Infrastructure depende de SDKs e implementa as abstrações.

---

# 3. Organização por domínio

## 3.1 Módulos propostos

O plano de controle deve ser dividido, no mínimo, nos seguintes módulos:

| Módulo | Responsabilidade | Não pode |
|---|---|---|
| Tenancy | tenant, projeto, isolamento e escopo | conhecer Purview ou EV |
| Projects | projeto, onda, aprovação e planejamento | processar bytes de PST |
| Sources | inventário e identidade da origem | chamar SDK de destino |
| Export | coordenação de exportação EV | executar script arbitrário |
| Artifacts | artefatos, hashes, lineage e imutabilidade | alterar artefato aprovado |
| Planning | capacidade, quotas, partes e destino | ignorar capability gate |
| Destinations | contrato de ingestão e resultado | conhecer detalhes de EV |
| Reconciliation | esperado × observado | declarar sucesso sozinho |
| Evidence | cadeia de custódia e pacote de evidência | permitir overwrite |
| Operations | retries, DLQ, quarentena, intervenção | esconder ação humana |

## 3.2 Agregados e invariantes

Os nomes finais podem mudar, mas os conceitos devem permanecer claros.

### MigrationProject

Invariantes:

- pertence a um tenant;
- possui owner e destino autorizado;
- configuração é versionada;
- políticas que alteram resultado possuem versão e hash;
- não inicia sem capability assessment válido.

### MigrationWave

Invariantes:

- conjunto fechado de artefatos aprovados;
- destino e target root definidos;
- não permite alterar bytes após aprovação;
- nova seleção cria nova versão da onda.

### SourceArchive

Invariantes:

- origem identificada;
- owner resolvido;
- capability discovery registrado;
- versão/build do EV não implica suporte por si só.

### MigrationArtifact

Invariantes:

- hash SHA-256;
- tamanho;
- origem;
- lineage;
- estado;
- path protegido;
- versão da ferramenta que o produziu;
- nunca sobrescrito depois de aprovado.

### ExternalOperation

Invariantes:

- `operation_key` determinística e única;
- estados explícitos;
- resultado ambíguo nunca gera reenvio automático;
- `COMPLETED` somente após confirmação do provedor.

## 3.3 Regras de negócio duras

Devem existir como invariantes de domínio, não apenas validação de UI:

- volume planejado acima de 100 GB para o mesmo archive: `MICROSOFT_ASSESSMENT_REQUIRED`;
- mapping CSV Purview: máximo de 500 linhas;
- `TargetRootFolder` inválido é bloqueado;
- replay usa os mesmos bytes, hash, mailbox e target root;
- ausência de capability evidence bloqueia;
- ausência de consentimento, licença, identidade ou capacidade bloqueia;
- Graph PST/EV → FTS permanece bloqueado pela capability específica;
- ambiente EV sem adapter certificado fica assistido ou bloqueado;
- nenhum dado de um usuário é redistribuído silenciosamente para outro archive.

---

# 4. Camadas e dependências

## 4.1 Domain

Pode depender apenas de:

- BCL;
- tipos próprios;
- shared kernel mínimo e estável.

Não pode depender de:

- EF Core;
- Dapper;
- SQL Client;
- ASP.NET Core;
- SDK Microsoft Graph;
- Exchange Online;
- AzCopy;
- Veritas;
- PowerShell SDK;
- filesystem concreto;
- logging framework;
- serializador de fornecedor.

O domínio deve conter:

- entidades;
- value objects;
- invariantes;
- serviços de domínio;
- eventos de domínio;
- códigos de erro;
- políticas puras.

## 4.2 Application

Contém:

- casos de uso;
- comandos e consultas;
- portas;
- coordenação de transações;
- autorização contextual;
- validações de aplicação;
- DTOs de fronteira interna.

Não pode conhecer detalhes de SQL, NTFS, Purview, Graph ou EV.

## 4.3 Infrastructure e Adapters

Contém:

- SQL Server;
- filesystem/NAS/SMB;
- adapters EV;
- adapter Purview;
- adapter Graph condicional;
- autenticação;
- criptografia;
- wrappers de processos;
- telemetria concreta.

Cada SDK externo deve ficar restrito ao adapter correspondente.

## 4.4 Composition roots

Somente API e workers registram dependências concretas.

Nenhuma classe de domínio deve chamar `new SqlConnection`, `new GraphServiceClient` ou ler `IConfiguration`.

## 4.5 Testes de arquitetura obrigatórios

O build deve falhar quando:

- Domain referencia Infrastructure;
- módulo A referencia Infrastructure do módulo B;
- projeto fora de `Target.Purview` conhece schema Purview;
- projeto fora de `Source.EnterpriseVault` conhece tipos Veritas;
- Graph é acessível quando capability está bloqueada;
- API ou Portal referencia biblioteca PST;
- worker de PST referencia credencial Purview;
- worker de upload recebe acesso ao PST original sem necessidade.

---

# 5. Estado, stateless e escalabilidade

## 5.1 Regra

API, Portal, Orchestrator e workers não podem usar memória local como fonte autoritativa de estado.

Estado durável deve estar em:

- SQL Server;
- artefatos imutáveis;
- storage protegido;
- ledger;
- outbox/inbox;
- checkpoints.

## 5.2 O que pode ficar em memória

Somente estado transitório e reconstruível:

- cache com expiração;
- buffer limitado;
- objetos do request atual;
- progresso não confirmado;
- credencial efêmera protegida durante a chamada.

Após crash, outra instância deve conseguir retomar pelo último checkpoint confirmado.

## 5.3 Proibições

- sessão de usuário em memória;
- singleton com estado de negócio;
- fila `ConcurrentQueue` como mecanismo durável;
- lock em memória para exclusão distribuída;
- dicionário local como registry de jobs;
- sticky session como requisito;
- progresso considerado confirmado antes de persistir.

---

# 6. Concorrência e execução durável

## 6.1 Fila SQL

A fila inicial é implementada no SQL Server.

Tabelas mínimas:

```text
job_queue
job_attempts
worker_leases
outbox_messages
inbox_messages
dead_letter_jobs
external_operations
```

## 6.2 Aquisição de job

A aquisição deve ser:

- transacional;
- exclusiva;
- com `UPDLOCK`;
- com `READPAST`;
- com lease;
- com `owner_worker`;
- com `lease_epoch`;
- com incremento de tentativa;
- com horário do banco.

O `rowversion` não é token de fencing.

## 6.3 Fencing

Heartbeat, checkpoint e conclusão devem ser condicionados a:

```text
job_id
owner_worker
lease_epoch
```

Se zero linhas forem afetadas:

1. o worker perdeu a propriedade;
2. deve interromper o processamento;
3. não pode persistir checkpoint;
4. não pode marcar sucesso;
5. qualquer efeito externo deve entrar em reconciliação.

O `lease_epoch` muda somente em nova aquisição.

## 6.4 Heartbeat

Regras:

- intervalo menor que o lease;
- recomendação inicial: `leaseDuration / 3`;
- falhas consecutivas limitadas;
- worker não pode continuar indefinidamente sem confirmar o lease;
- relógio de referência é o SQL Server.

## 6.5 Reaper

Dois fluxos:

### Job puramente local

Lease expirado:

- volta para `PENDING`, ou
- vai para `DEAD_LETTER` ao exceder tentativas.

### Job com possível efeito externo

Lease expirado:

- vai para `RECOVERY_REQUIRED` ou `RECONCILING`;
- nunca volta automaticamente para `PENDING`;
- consulta o provedor antes de nova tentativa.

## 6.6 External operations ledger

Estados:

```text
INTENT
SUBMITTED
CONFIRMED
AMBIGUOUS
FAILED
```

Fluxo:

1. gerar `operation_key`;
2. gravar `INTENT` com unique constraint;
3. commitar SQL;
4. chamar o provedor;
5. gravar provider operation ID;
6. marcar `SUBMITTED` ou `CONFIRMED`;
7. em timeout ou resposta incerta, marcar `AMBIGUOUS`;
8. reconciliar;
9. nunca repetir automaticamente enquanto o resultado estiver ambíguo.

## 6.7 Anti-starvation

Não usar apenas:

```sql
ORDER BY priority DESC, enqueued_at ASC
```

Adotar mecanismo verificável:

- aging como padrão; ou
- quota por prioridade; ou
- weighted round-robin.

Teste obrigatório: jobs de baixa prioridade devem ser processados dentro do limite de espera definido, mesmo com chegada contínua de prioridade alta.

## 6.8 HA

No perfil HA:

- ledger e fila em commit síncrono;
- failover automático somente entre réplicas síncronas;
- réplica assíncrona apenas para DR/leitura;
- failover forçado exige modo de desastre e reconciliação;
- ausência de linha no ledger após desastre não autoriza reenvio sem consulta ao provedor.

---

# 7. Concorrência em arquivos e artefatos

## 7.1 Imutabilidade

Artefatos aprovados nunca são editados.

Nova transformação cria:

- novo artifact ID;
- novo hash;
- novo path;
- vínculo `derived_from`;
- versão da ferramenta.

## 7.2 Escrita segura

Fluxo obrigatório:

1. criar arquivo temporário exclusivo;
2. impedir follow de symlink/reparse point;
3. escrever;
4. flush;
5. calcular hash;
6. validar tamanho;
7. persistir metadados;
8. mover atomicamente para nome final;
9. tornar somente leitura quando aplicável;
10. nunca sobrescrever arquivo existente.

## 7.3 Estrutura de path

Todo path deve incluir escopo lógico:

```text
<root>/<tenant>/<project>/<job>/<artifact-id>/<version>/
```

Não confiar apenas em ACL. O escopo também deve existir no banco e na autorização.

## 7.4 Path traversal

Obrigatório:

- canonicalizar path;
- garantir que o path final permaneça sob a raiz permitida;
- rejeitar `..`;
- rejeitar paths absolutos recebidos externamente;
- validar junctions, symlinks e reparse points;
- não usar nome original de arquivo diretamente;
- sanitizar caracteres e colisões de case.

## 7.5 Lock

Locks de arquivo devem ser curtos. Coordenação de negócio deve permanecer no SQL.

Não manter lock de arquivo durante chamada externa longa.

---

# 8. Integrações externas

## 8.1 Regra de isolamento

Cada integração externa é um adapter.

O núcleo não conhece:

- endpoint;
- SDK;
- autenticação;
- payload proprietário;
- throttling específico;
- códigos HTTP específicos.

O adapter traduz erros para uma taxonomia interna.

## 8.2 Capability first

Antes de executar:

1. descobrir capability;
2. registrar versão/build/provider;
3. validar evidence;
4. validar tenant e escopo;
5. decidir `ENABLED`, `CONDITIONAL`, `BLOCKED_PENDING_EVIDENCE` ou `DISABLED`;
6. persistir decisão e motivo.

Não usar `if (version >= X)` como prova de suporte.

## 8.3 Enterprise Vault

- selecionar adapter por capability discovery;
- PowerShell somente assinado e versionado;
- allowlist de comandos;
- input/output JSON;
- nenhuma execução arbitrária enviada pelo Control Plane;
- versão é candidatura, não certificação;
- falha de discovery resulta em modo assistido ou bloqueio.

## 8.4 Purview

- adapter isolado;
- pré-checks antes do upload;
- SAS nunca em log;
- mapping CSV validado;
- portal tratado como tarefa humana auditada;
- quatro-olhos para início da importação;
- resultado do portal não encerra o job sem reconciliação;
- retry reutiliza artefato e target root;
- resposta ambígua entra no ledger.

## 8.5 Graph

- adapter condicional;
- rota PST/EV → FTS bloqueada até capability evidence;
- teste deve garantir que o bloqueio não seja removido por configuração comum;
- promoção exige configuração versionada e evidence aprovada.

---

# 9. Tratamento de erro, retry e DLQ

## 9.1 Taxonomia

Categorias mínimas:

- `VALIDATION`;
- `AUTHORIZATION`;
- `CAPABILITY_BLOCKED`;
- `TRANSIENT_PROVIDER`;
- `PERMANENT_PROVIDER`;
- `CONCURRENCY_LOST`;
- `AMBIGUOUS_EXTERNAL_RESULT`;
- `ARTIFACT_INTEGRITY`;
- `SECURITY_POLICY`;
- `RESOURCE_EXHAUSTION`;
- `OPERATOR_ACTION_REQUIRED`.

## 9.2 Retry

Retry somente para erro classificado como transitório.

Obrigatório:

- max attempts;
- exponential backoff;
- jitter;
- `visible_at`;
- correlation ID;
- último erro sanitizado;
- contagem de tentativas;
- idempotency key.

Não repetir:

- validação inválida;
- capability bloqueada;
- erro de autorização permanente;
- hash divergente;
- resultado externo ambíguo sem reconciliação;
- ação humana pendente.

## 9.3 Dead letter

DLQ:

- não é reprocessada automaticamente;
- exige decisão do operador;
- registra motivo;
- preserva evidence;
- possui runbook;
- reprocessamento cria nova tentativa auditada.

---

# 10. Segurança por design — SSDLC

## 10.1 Segurança começa no planejamento

Cada vertical slice deve conter:

- ativos;
- dados sensíveis;
- atores;
- fronteiras de confiança;
- entradas não confiáveis;
- ameaças;
- controles;
- testes;
- risco residual.

Sem threat-model delta, a feature não está pronta para desenvolvimento.

## 10.2 Threat modeling

Usar STRIDE como checklist, sem transformar o processo em burocracia.

### Spoofing

- identidade de worker;
- identidade de operador;
- certificado de source connector;
- tenant/mailbox alvo.

### Tampering

- PST;
- manifestos;
- mapping CSV;
- scripts EV;
- logs;
- provider operation ID.

### Repudiation

- aprovação;
- intervenção;
- retry;
- mudança de política;
- operação no portal.

### Information Disclosure

- corpo, assunto, anexos;
- paths reais;
- SAS;
- tokens;
- certificados;
- nomes de arquivo sensíveis.

### Denial of Service

- PST malformado;
- arquivo gigante;
- zip bomb equivalente;
- fila saturada;
- scratch cheio;
- provider throttling.

### Elevation of Privilege

- worker como administrador;
- gMSA excessiva;
- execução de PowerShell arbitrário;
- path traversal;
- injeção SQL ou command injection.

## 10.3 Segredos

Nunca:

- commit;
- variável de log;
- argumento de linha de comando;
- arquivo temporário permissivo;
- configuração versionada.

Preferências on-premises:

- gMSA;
- Windows Certificate Store;
- ACL;
- DPAPI somente em nó único;
- mecanismo compartilhado e protegido em HA;
- rotação;
- expiração;
- break-glass auditado.

## 10.4 Processos externos

Ao chamar AzCopy, PowerShell ou executável:

- nunca montar comando por concatenação livre;
- usar argumentos estruturados;
- allowlist de opções;
- timeout;
- cancelamento;
- stdout/stderr sanitizados;
- working directory controlado;
- usuário sem privilégio;
- executable pinado e verificado;
- validar exit code e output;
- não colocar segredo em argv.

## 10.5 Logs

Campos permitidos:

- timestamp UTC;
- event name;
- tenant HMAC;
- project ID;
- job ID;
- artifact ID;
- wave ID;
- attempt ID;
- worker instance;
- correlation ID;
- trace ID;
- duration;
- bytes;
- item count;
- outcome;
- error code.

Campos proibidos:

- subject;
- body;
- attachment;
- token;
- SAS;
- senha;
- conteúdo de certificado privado;
- path real;
- conteúdo de e-mail.

## 10.6 Privilégio mínimo

- serviço nunca Domain Admin;
- worker PST não possui permissão M365;
- upload worker não acessa origem quando não necessário;
- recon worker não altera artefato;
- evidence service não permite overwrite;
- portal não processa PST;
- API não processa PST no request thread.

---

# 11. Testes automatizados

## 11.1 Pirâmide obrigatória

### Unit

Foco:

- invariantes;
- value objects;
- canonicalização;
- políticas;
- state machines;
- planning;
- validators;
- error mapping.

Sem banco, rede ou filesystem real.

### Architecture

Foco:

- dependências;
- isolamento de módulos;
- SDKs externos;
- composition roots;
- capability gates.

### Contract

Foco:

- Purview CSV;
- EV JSON/schema;
- Graph HTTP;
- ledger;
- manifestos;
- error codes;
- eventos.

### Integration

Foco:

- SQL Server real;
- transações;
- locks;
- leases;
- outbox/inbox;
- filesystem;
- ACL;
- processo externo controlado;
- worker restart.

### Compatibility

Foco:

- versões EV;
- PowerShell;
- AzCopy;
- SQL Server suportado;
- Windows Server;
- SDKs;
- módulos Exchange Online;
- formatos PST.

### E2E

Fluxo:

```text
origem sintética
→ inventário
→ export/preparo
→ hash
→ planejamento
→ upload simulado/controlado
→ tarefa humana
→ reconciliação
→ evidence
```

### Performance

Medir:

- claim P95;
- contenção SQL;
- throughput;
- CPU;
- RAM;
- scratch;
- latência por mailbox;
- backlog;
- aging;
- capacidade de retomada.

### Chaos

Cenários mínimos:

- kill do worker;
- SQL indisponível;
- disco cheio;
- NAS indisponível;
- DNS;
- perda de rede;
- SAS expirado;
- 429/5xx;
- resposta externa ambígua;
- credencial revogada;
- arquivo alterado durante leitura;
- failover SQL.

### Security

- cross-tenant;
- path traversal;
- symlink/reparse;
- command injection;
- SQL injection;
- autorização;
- secret leakage;
- log redaction;
- capability bypass;
- replay inseguro;
- arquivo hostil;
- dependência vulnerável.

### Recovery

- backup/restore;
- reconstrução da fila;
- retomada por checkpoint;
- ledger após failover;
- recuperação de evidence;
- RTO/RPO medidos.

## 11.2 Cobertura

Cobertura é gate, não objetivo final.

Baseline proposta:

- Domain + Application: 85% line e 80% branch;
- módulos críticos de idempotência, planejamento, isolamento e ledger: 90% branch;
- solução total: 70% line;
- mutation testing em módulos críticos;
- nenhum código crítico excluído artificialmente.

Alteração desses números exige PR documentado.

## 11.3 Testes de concorrência obrigatórios

- N workers disputando a mesma fila;
- nenhum job processado simultaneamente por dois owners válidos;
- lease epoch antigo rejeitado;
- heartbeat concorrente;
- checkpoint concorrente;
- completion após lease perdido;
- crash após efeito externo e antes do update SQL;
- anti-starvation;
- deadlock detectado e tratado;
- failover durante INTENT/SUBMITTED/CONFIRMED.

---

# 12. SAST, DAST, SCA e supply chain

## 12.1 SAST

Executar em todo PR.

Bloquear:

- injection;
- uso inseguro de crypto;
- path traversal;
- command construction;
- deserialização insegura;
- segredo;
- código de alto risco sem justificativa.

Achado crítico ou alto: não mergear.

## 12.2 DAST

Executar contra ambiente de teste implantado internamente.

Cobrir:

- autenticação;
- autorização;
- headers;
- TLS;
- endpoints administrativos;
- input validation;
- upload;
- rate limiting;
- sessão;
- disclosure.

DAST não substitui teste de autorização específico.

## 12.3 SCA

- versões exatas;
- lock files;
- restore locked;
- scan transitivo;
- CVEs;
- licença;
- pacote abandonado;
- SBOM CycloneDX/SPDX.

Dependência com CVE crítico sem correção:

- bloquear;
- avaliar mitigação;
- documentar risco;
- exigir aceite formal de segurança para exceção temporária.

## 12.4 Provenance

- build determinístico;
- artifact identificado;
- hash;
- SBOM;
- versão do commit;
- assinatura;
- origem do pipeline;
- publicação em registry/repositório privado.

---

# 13. CI/CD on-premises

## 13.1 Pipeline de pull request

Ordem recomendada:

1. checkout por SHA;
2. verificar branch policy;
3. secret scanning;
4. restore locked;
5. verificação de formatação;
6. build Release determinístico;
7. unit tests;
8. architecture tests;
9. contract tests;
10. integration tests;
11. compatibility smoke;
12. security tests;
13. SAST;
14. SCA e licença;
15. SBOM;
16. package scan;
17. DAST em ambiente efêmero quando aplicável;
18. gerar artifacts e provenance;
19. assinar;
20. publicar somente no repositório privado.

Passos Azure/Bicep do runbook v1.0 não são baseline obrigatória após o ADR-0003. Não introduzir Azure no pipeline principal.

## 13.2 Promoção

- build uma vez;
- promover o mesmo artifact;
- nunca recompilar para produção;
- dev após merge;
- test exige integração e compatibility;
- staging exige change record;
- produção exige dois aprovadores;
- rollback plan;
- backup validado;
- smoke test;
- observação pós-deploy.

## 13.3 Branch protection

Obrigatório:

- CI verde;
- review;
- conversations resolvidas;
- sem force push após Ready, salvo correção explícita;
- sem auto-merge por agente;
- merge somente pelo owner autorizado.

---

# 14. Banco de dados

## 14.1 Adapter de persistência

Domain não conhece SQL.

Application define portas específicas, por exemplo:

```csharp
public interface IJobQueue
{
    Task<ClaimedJob?> TryClaimAsync(
        WorkerIdentity worker,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}
```

Evitar `IGenericRepository<T>`.

## 14.2 Queries com isolamento

Toda tabela e query de negócio deve incluir tenant e projeto quando aplicável.

Proibido:

```csharp
GetByIdAsync(jobId)
```

Preferido:

```csharp
GetByIdAsync(tenantId, projectId, jobId)
```

Usar:

- composite keys;
- unique constraints escopadas;
- foreign keys;
- índices;
- autorização na Application;
- teste cross-tenant.

## 14.3 Migrações

- expand/contract;
- backward compatible;
- sem drop imediato;
- backup antes de mudança destrutiva;
- script revisável;
- timeout e lock controlados;
- dados migrados em lotes;
- rollback operacional documentado;
- schema version persistida.

## 14.4 Transações

Transação cobre apenas recursos locais do SQL.

Nunca alegar transação distribuída com Microsoft 365.

---

# 15. Observabilidade

## 15.1 Logs estruturados

Toda operação relevante deve possuir:

- event name estável;
- correlation ID;
- job ID;
- attempt ID;
- tenant HMAC;
- outcome;
- error code;
- duração;
- bytes/itens quando aplicável.

## 15.2 Métricas

Mínimas:

- jobs por estado;
- queue depth;
- claim latency P95;
- lease loss;
- retry;
- DLQ;
- ambiguous external operations;
- throughput;
- worker utilization;
- scratch usage;
- evidence completeness;
- blocked capability;
- cross-tenant denial;
- reconciliation mismatch.

## 15.3 Tracing

Trace entre:

```text
API
→ Orchestrator
→ Queue
→ Worker
→ Adapter
→ Provider
→ Reconciliation
```

Não incluir segredo no baggage.

## 15.4 Health

Separar:

- liveness;
- readiness;
- dependency health;
- degraded mode.

Readiness deve falhar quando a instância não puder aceitar trabalho com segurança.

---

# 16. Processo de desenvolvimento por vertical slice

## Etapa 1 — Selecionar slice

A slice deve entregar uma capacidade de negócio pequena e completa.

Exemplo:

```text
Registrar artefato imutável com hash e lineage
```

Não:

```text
Criar todas as classes base do sistema
```

## Etapa 2 — Definition of Ready

Antes de codificar, registrar:

- problema;
- ator;
- regra de domínio;
- entradas;
- saídas;
- estados;
- invariantes;
- erros;
- threat-model delta;
- telemetria;
- testes;
- migration;
- rollback;
- dependências;
- capability gate.

## Etapa 3 — Escrever testes de domínio

Criar primeiro testes das invariantes e decisões.

## Etapa 4 — Implementar Domain

- sem framework;
- tipos fortes;
- sem setters públicos indiscriminados;
- estados válidos;
- erro explícito;
- eventos de domínio somente quando necessários.

## Etapa 5 — Implementar Application

- caso de uso;
- autorização;
- transação;
- portas;
- idempotência;
- cancellation token;
- resultado tipado.

## Etapa 6 — Implementar adapter

- SDK isolado;
- timeout;
- retry classificado;
- logging sanitizado;
- error mapping;
- capability;
- contract tests.

## Etapa 7 — Composition root

Registrar implementação somente na API ou worker.

## Etapa 8 — Integration tests

Usar SQL Server e filesystem reais em ambiente de teste.

## Etapa 9 — Security validation

- SAST;
- secret scan;
- SCA;
- testes negativos;
- threat model atualizado.

## Etapa 10 — PR draft

Descrição obrigatória:

- problema;
- solução;
- ADRs aplicáveis;
- arquivos;
- invariantes;
- threat model;
- testes;
- coverage;
- migrations;
- rollback;
- observabilidade;
- riscos;
- limitações;
- evidência.

## Etapa 11 — Review

Corrigir apenas:

- blocker;
- falsidade técnica;
- risco de segurança;
- contradição arquitetural;
- falha de CI;
- dívida que inviabiliza operação.

Não gastar ciclos com microedição sem impacto.

## Etapa 12 — Ready e merge

Agente não marca Ready nem mergeia sem autorização do Decision Owner.

---

# 17. Code review

## 17.1 Checklist do revisor

### Domínio

- regra está no domínio?
- invariantes não dependem da UI?
- estados impossíveis foram eliminados?
- erro possui código estável?

### Arquitetura

- SDK externo está isolado?
- módulo violou fronteira?
- nova abstração é necessária?
- houve microserviço prematuro?
- existe acoplamento a SQL/framework no núcleo?

### Concorrência

- operação é idempotente?
- há unique constraint?
- lease/fencing está correto?
- resposta ambígua é reconciliada?
- arquivo pode ser sobrescrito?
- há race entre heartbeat/checkpoint/completion?

### Segurança

- least privilege?
- segredo em log/argv?
- path traversal?
- tenant scope?
- input hostil?
- threat model atualizado?

### Operação

- métricas?
- logs?
- retry?
- DLQ?
- rollback?
- runbook?

### Testes

- unit?
- architecture?
- integration?
- negativo?
- chaos/recovery quando necessário?
- teste falha antes da correção?

## 17.2 Revisão por pares

Funcionalidades críticas exigem senior da disciplina relevante:

- SQL/concorrência;
- segurança;
- EV;
- Purview/M365;
- jurídico/licenciamento.

Na ausência de revisor independente, registrar explicitamente a exceção. Não declarar “peer reviewed” quando o mesmo agente escreveu e revisou.

---

# 18. Definition of Done

Uma slice só está concluída quando:

- comportamento atende os critérios;
- invariantes cobertas;
- CI verde;
- testes de arquitetura verdes;
- testes de integração verdes;
- cobertura mínima;
- mutation testing crítico;
- SAST sem crítico/alto;
- secret scan limpo;
- SCA/SBOM;
- threat model atualizado;
- logs sanitizados;
- métricas;
- migrations;
- rollback;
- documentação;
- runbook;
- review;
- evidence anexada;
- nenhum capability gate contornado;
- nenhuma ação manual escondida;
- nenhum estado autoritativo somente em memória.

---

# 19. Stop-the-line — situações que obrigam bloquear

Parar o desenvolvimento ou a execução quando houver:

- documentação Microsoft divergente;
- API preview/privada no caminho GA;
- capacidade não comprovada;
- volume acima do permitido;
- identity mismatch;
- hash divergente;
- target root inseguro;
- tenant scope ausente;
- segredo exposto;
- resultado externo ambíguo;
- adapter EV não certificado;
- licença ausente;
- dependência crítica vulnerável;
- cross-tenant test falhando;
- evidence incompleta;
- rollback inexistente;
- alteração de arquitetura sem ADR;
- pressão para “funcionar no laboratório” usando método não suportado.

A resposta correta é bloquear com reason code, registrar evidence e escalar.

---

# 20. Anti-patterns proibidos

- `if (provider == "Purview")` espalhado;
- `catch (Exception) { return true; }`;
- retry infinito;
- `Task.Run` para esconder operação longa;
- fire-and-forget;
- lock em memória para coordenação distribuída;
- singleton stateful;
- PST em mensagem de fila;
- PST no SQL;
- segredo em appsettings;
- SAS em log;
- PowerShell arbitrário;
- path baseado diretamente no nome enviado;
- `latest` em dependência ou imagem;
- wildcard de pacote;
- biblioteca externa no Domain;
- microserviço por módulo;
- generic repository universal;
- mapper automático escondendo regras de domínio;
- status `Completed` sem reconciliação;
- recriar import após timeout sem consultar provedor;
- usar `rowversion` como lease fencing token;
- failover assíncrono como zero-data-loss;
- teste que depende de `sleep` em vez de sincronização observável;
- cobertura inflada por testes sem assertiva relevante.

---

# 21. Diretriz imediata ao Claude/desenvolvedor

```text
Você trabalhará exclusivamente no repositório
viniciusmiranda022-pixel/ArchiveBridge.

Não acessar, analisar ou alterar outros repositórios.

Antes de qualquer código, leia:
- ADR-0001;
- ADR-0002;
- ADR-0003;
- ADR-0007;
- ADR-0013;
- evidências vinculadas;
- matriz de gates;
- este runbook.

Estado atual (28/07/2026):
- 0001, 0002, 0003, 0007 e 0013 aceitos;
- 0006 aceito (arquitetural; Gate A do Purview obrigatório antes de producao);
- 0008 aceito arquiteturalmente com condicoes (assinatura Seguranca/DPO anterior a producao; HA de segredos BLOCKED_PENDING_EVIDENCE);
- 0005 aceito como decisao de nao inclusao no MVP (libpff = capacidade opcional BLOCKED_PENDING_EVIDENCE, nao bloqueadora; MVP nao distribui libpff);
- scaffolding e codigo de produto continuam bloqueados ate os merges documentais em main e a confirmacao final do Decision Owner.

Ate essa liberacao:
- produzir apenas ADR, evidência, threat model, contrato e plano de teste;
- um ADR por branch;
- um PR draft por ADR;
- não aceitar;
- não marcar Ready;
- não mergear;
- não habilitar auto-merge;
- não criar scaffolding.

Depois da liberação para codificar:
- monólito modular no Control Plane;
- workers isolados;
- arquitetura hexagonal;
- Domain sem frameworks/SDKs;
- SQL Server local como sistema de registro;
- fila durável SQL;
- storage local/NAS/SMB;
- M365 somente como destino externo;
- nenhuma dependência obrigatória de Azure PaaS;
- microsserviços proibidos sem novo ADR;
- todo efeito externo idempotente e reconciliável;
- todo estado autoritativo durável;
- fail closed;
- capability first;
- SSDLC e threat modeling por slice;
- SAST, DAST, SCA, SBOM e secret scan;
- testes unitários, arquitetura, contrato, integração, compatibilidade,
  E2E, performance, chaos, segurança e recovery;
- CI deve bloquear violações;
- código crítico sem teste não é concluído;
- revisão automática deve ser avaliada pelo mérito;
- corrigir blockers, erros técnicos, segurança, arquitetura ou CI;
- evitar microedições sem valor;
- nunca marcar Ready ou mergear sem autorização de Vinicius Miranda.
```

---

# 22. Referências obrigatórias no repositório

- `docs/adr/0001-monolito-modular-e-workers-isolados.md`
- `docs/adr/0002-dotnet-10-lts-e-politica-de-atualizacao.md`
- `docs/adr/0003-azure-sql-e-service-bus-premium.md`
- `docs/adr/0007-graph-fts-bloqueado.md`
- `docs/adr/0013-exportacao-ev-multiversao.md`
- `docs/adr/gate-closure-matrix.md`
- `docs/runbook/02-parte-ii-arquitetura.md`
- `docs/runbook/03-parte-iii-conectores-e-engine-pst.md`
- `docs/runbook/04-parte-iv-destinos-m365.md`
- `docs/runbook/05-parte-v-seguranca-infra-operacao.md`
- `docs/runbook/06-parte-vi-plano-aceitacao.md`
- `docs/ev/`

---

## Registro final

Este runbook deve ser versionado por PR antes do início do scaffolding. Mudanças materiais exigem revisão do Decision Owner. Regras de arquitetura, segurança, concorrência e suporte não podem ser relaxadas por conveniência de implementação.
