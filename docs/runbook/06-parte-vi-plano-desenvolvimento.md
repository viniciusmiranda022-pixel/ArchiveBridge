<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Parte VI - Plano de desenvolvimento e aceitação de produção

## 43. Estratégia de entrega sem protótipo descartável

O projeto será desenvolvido em incrementos de produção. Cada incremento usa arquitetura final, IaC, segurança, telemetria e testes. Não existe código “para jogar fora”. Ainda assim, antes de dados críticos, o produto precisa passar por canário controlado. Remover essa etapa aumentaria risco de duplicidade e perda, não velocidade.

### 43.1 Equipe mínima realista

| **Papel** | **Quantidade** | **Responsabilidade** |
| --- | --- | --- |
| Principal/Systems Architect | 1 | decisões, gates externos e arquitetura |
| Backend .NET | 3 | domínio, API, orchestrator, adapters |
| Windows/EV Engineer | 1 | connector, EV, hardening, workers |
| Frontend | 1 | portal e workflow de aprovação |
| QA/SDET | 1–2 | corpus, automação, compatibilidade, chaos |
| Cloud/SRE | 1 | IaC, pipelines, observabilidade, DR |
| Security Engineer | 0,5–1 | threat model, AppSec, pen-test |
| Product/Domain Owner | 1 | regras, prioridades, aceite e fornecedor |

Com menos de seis profissionais dedicados, reduzir escopo/conectores; não reduzir controles críticos.

### 43.2 Sequência e duração indicativa

| **Incremento** | **Semanas** | **Entregável de produção** |
| --- | --- | --- |
| I0 - Foundation | 1–4 | repo, ADRs, IaC dev, identity, CI, skeleton, threat model |
| I1 - Custody/Ingest | 5–8 | source registry, secure landing, hash, evidence chain |
| I2 - PST Inspection | 9–13 | Aspose adapter, validator, corpus, risk scoring |
| I3 - Partition | 14–19 | planner, splitter, manifests, restart/checkpoints |
| I4 - EV Connector | 16–21 | inventory/export adapter, outbound enrollment, audit |
| I5 - Purview Adapter | 20–25 | prechecks, SAS JIT, AzCopy, CSV, operator tasks |
| I6 - Reconciliation | 24–29 | EXO stats, results, exception workflow, certificate |
| I7 - Hardening | 28–34 | HA, DR, chaos, performance, WDAC, pen-test, SLO |
| I8 - Production Acceptance | 35–38 | canário, operational readiness, go-live |

Trilhas se sobrepõem após interfaces estabilizadas. Um produto com pretensão de superar soluções maduras exigirá mais ciclos, conectores e benchmarks; 38 semanas é baseline para uma primeira versão de produção com Purview GA, não prova de superioridade universal.

## 44. Backlog por épico

### EPIC-01 Tenancy e autorização

- criar tenant/project e policies;
- resolver identity claims para roles internas;
- testar cross-tenant em todos os repositories/endpoints;
- PIM/Conditional Access operacional;
- auditoria de permission change.

**Aceite:** suite de isolation tests verde; tentativa cruzada retorna 404/403 sem revelar existência; zero consulta sem tenant.

### EPIC-02 Custody e artifacts

- canonical hash;
- immutable artifact registry;
- lineage source → repaired → part;
- custody events encadeados;
- evidence WORM e signature verifier.

**Aceite:** alterar um byte invalida validação; pacote de evidência é verificável offline.

### EPIC-03 PST Inspector

- abrir Unicode/ANSI;
- árvore e contagens;
- classes/datas/anomalias;
- senha/corrupção;
- resource limits e cancelamento.

**Aceite:** corpus com PSTs 1 MB a 500 GB sintético/esparso e datasets reais autorizados; uso de memória controlado; nenhum conteúdo em logs.

### EPIC-04 Partition Engine

- size split;
- semantic plan;
- part manifest;
- restart;
- immutable output;
- engine versioning.

**Aceite:** parts abaixo do limit, contagem/fingerprint fechando, crash recovery e reabertura.

### EPIC-05 Enterprise Vault

- enrollment;
- inventory;
- export commands;
- thread throttling;
- oversized native items;
- delta strategy por versão.

**Aceite:** nenhum inbound, nenhum Domain Admin, export/retry auditado e impacto dentro do budget.

### EPIC-06 Purview

- tenant/mailbox prechecks;
- capability registry;
- secure SAS form;
- AzCopy wrapper;
- mapping builder/validator;
- portal tasks e attachments.

**Aceite:** bloqueios de 100 GB/500 rows/root slash; nenhum SAS em log; retries usam mesmo artifact/root.

### EPIC-07 Reconciliation

- service result importer;
- EXO stats before/after;
- expected vs observed;
- exception disposition;
- certificate.

**Aceite:** job não fecha sem 100% evidence completeness; desvios materializados por código.

### EPIC-08 Platform/SRE

- Bicep, policies, private endpoints;
- Service Bus, SQL, storage, Key Vault;
- dashboards, alerts, backups;
- DR exercise;
- cost telemetry.

**Aceite:** environment recreated from zero; restore exercise dentro do RTO/RPO.

## 45. Test strategy

### 45.1 Pirâmide

- unit: domínio, canonicalization, plan e validators;
- architecture: dependências e limites;
- contract: Purview CSV, Graph HTTP, EV schema;
- integration: SQL/Service Bus/Blob/Key Vault e workers;
- compatibility: versões SDK, PST engines, EV, AzCopy, EXO module;
- end-to-end: source sintético → part → provider tenant → recon;
- performance: tamanhos/quantidades e profiles;
- chaos: crash, timeout, disk full, network loss, SAS expiry;
- security: authz, isolation, secrets, path, queue, supply chain;
- recovery: backup/restore e reconstrução de fila.

### 45.2 Corpus PST

| **Classe** | **Exemplos** |
| --- | --- |
| formato | ANSI, Unicode, diferentes versões |
| tamanho | pequeno, 18 GB boundary, 20 GB boundary, 50, 100, 500 GB |
| conteúdo | mail, calendar, contacts, tasks, notes, distribution lists |
| estrutura | pastas profundas, caracteres Unicode, case conflicts, 100k+ items |
| anomalia | corrupção leve/persistente, senha, truncado, hash changing |
| datas | sem data, antiga, futura, timezone/DST |
| itens | attachment grande, recurring meetings, S/MIME, custom MAPI props |
| overlap | mesmo PST/retry; PST diferente com conteúdo igual |

PSTs gigantes de teste podem usar dataset sintético controlado; sparse file sozinho não testa parser. Dados reais precisam de autorização, mascaramento quando possível e ambiente isolado.

### 45.3 Testes de invariantes

```csharp
[Fact]
public void TargetRootFolder_Root_IsRejected()
{
    var act = () => TargetRootFolder.Create("/");
    act.Should().Throw<DomainRuleException>()
       .WithMessage("*root folder*not allowed*");
}

[Fact]
public void PurviewWave_OverMainArchiveLimit_IsBlocked()
{
    var wave = WaveBuilder.ForArchive()
        .WithPlannedBytes(101L * 1024 * 1024 * 1024)
        .Build();

    var result = PurviewPolicy.Evaluate(wave, Capabilities.Default);
    result.Code.Should().Be("M365_ARCHIVE_IMPORT_LIMIT");
}
```

### 45.4 Chaos cases

164. matar worker durante escrita de part;
165. reiniciar após hash e antes do custody event;
166. expirar lease com worker ainda vivo;
167. SQL indisponível após upload, antes do state update;
168. Service Bus entrega duplicado;
169. SAS expira durante upload;
170. DNS falha; rede perde pacotes;
171. scratch fica sem espaço;
172. log sink indisponível;
173. provider retorna 429/5xx/ambíguo;
174. identity permission é removida;
175. arquivo origem muda no meio.

Cada teste prova que o sistema não duplica efeito, não perde evidência e não declara sucesso indevido.

## 46. Performance e dimensionamento

### 46.1 Perfis iniciais de worker

| **Perfil** | **CPU** | **RAM** | **Scratch** | **Uso** |
| --- | --- | --- | --- | --- |
| Inspector | 8 vCPU | 32 GiB | 512 GiB | PSTs até ~100 GB |
| Heavy PST | 16–32 vCPU | 64–128 GiB | 1–2 TiB NVMe/SSD | 100–500+ GB, repair/split |
| Validator | 4–8 vCPU | 16–32 GiB | 256–512 GiB | independent scan/hash |
| Upload | 4–8 vCPU | 16 GiB | cache mínimo | AzCopy/network |

São estimativas, não mínimos garantidos. O benchmark deve medir throughput de leitura/escrita, IOPS, CPU, working set e wall time por engine/version. Um PST de 500 GB exige scratch para original derivado + parts + overhead; capacity planner impede iniciar sem margem.

### 46.2 Capacity formula

```text
requiredScratch =
  sourceCopyBytes (se local) +
  expectedPartBytes +
  repairBackupBytes +
  engineTemporaryOverhead +
  safetyMargin(20%)
```

### 46.3 Provider throughput

A Microsoft informa aproximadamente 24 GB/dia por mailbox como taxa típica, não SLA. Parts para a mesma mailbox competem/serializam no destino; escala horizontal vem de mailboxes diferentes. O scheduler deve limitar concorrência por mailbox e aprender a baseline observada por tenant.

## 47. Production readiness review

### 47.1 Gate de arquitetura

- ADRs aprovados;
- diagramas e data flow atualizados;
- capability matrix atual;
- nenhum preview no caminho GA;
- owner de cada serviço.

### 47.2 Gate de segurança

- threat model fechado;
- pen-test sem crítico/alto aberto;
- secrets scan limpo;
- SBOM e assinaturas;
- WDAC/Defender/patching;
- cross-tenant tests;
- incident response exercitado.

### 47.3 Gate de dados

- hashes, manifests, lineage e WORM;
- privacy impact assessment;
- retention/deletion documentada;
- backup/restore testado;
- corpus/fidelity report aprovado.

### 47.4 Gate operacional

- dashboards e alertas;
- on-call e escalation;
- DLQ/retry/quarantine runbooks;
- capacity/FinOps;
- RTO/RPO exercitados;
- support package automation.

### 47.5 Gate Microsoft 365

- roles mínimas;
- tenant precheck;
- archive/licença/quota;
- AzCopy version homologada;
- mapping validator;
- target root policy;
- limite 100 GB/500 linhas;
- portal operator treinado.

## 48. Canário de produção

Embora não haja “piloto descartável”, a ativação usa canário:

176. tenant controlado, mailbox de teste licenciada;
177. 20 tipos de item e propriedades customizadas;
178. PST pequeno, depois 18 GB boundary;
179. replay do mesmo PST no mesmo target root;
180. tentativa deliberada de target root diferente deve bloquear;
181. corrupção conhecida e quarantine;
182. crash recovery;
183. reconciliação e evidence package;
184. restore/rollback operacional;
185. approval para primeira onda real de baixa criticidade.

O mesmo build/digest do canário é promovido; não existe fork de “produção”.

## 49. Critérios de encerramento de uma migração

Um projeto só fica `COMPLETED` quando:

- escopo e policy version estão assinados;
- todas as fontes têm disposition;
- todas as parts estão importadas, filtradas por política ou em exceção aprovada;
- resultados do provider foram coletados;
- reconciliação fechou;
- holds/retention foram revisados pelo owner;
- usuários e inativos foram tratados conforme mapeamento;
- pacote de evidência foi assinado e publicado WORM;
- janela de rollback e decommission foram definidas;
- cliente aprovou relatório final;
- nenhuma credencial temporária permanece ativa.

## 50. Decisão final para o caso de 500 GB

Construir a plataforma é viável e faz sentido. Importar 500 GB de um único PST para o mesmo Online Archive com apenas o Purview Network Upload público não deve ser prometido. O produto entrega valor real ao industrializar export, inspeção, partição, evidência, transporte, bloqueios e reconciliação. Para cumprir integralmente 500 GB por usuário, será necessário um destes eventos:

- Microsoft aprovar formalmente o cenário e fornecer procedimento suportado;
- Graph/FTS para archive tornar-se GA e aceitar o caminho PST/EV homologado;
- a empresa licenciar/acessar um protocolo de ingestão avançada suportado;
- o cliente aprovar redesign de destino/retenção que permaneça conforme as regras.

> [!TIP]
> **CRITÉRIO DE APROVAÇÃO**
> **Go para desenvolver o núcleo e o adapter Purview suportado. No-go para vender “500 GB no mesmo archive” como capacidade atual.** O capability gate transforma essa limitação externa em comportamento controlado, sem contaminar o produto ou exigir reescrita futura.
