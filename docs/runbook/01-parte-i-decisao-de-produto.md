<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Parte I - Decisão de produto e limites reais da plataforma

## 1. Mandato, resultado e regra de honestidade técnica

O produto descrito neste runbook será uma plataforma de migração industrial de dados de correio legados, com foco inicial em Enterprise Vault e arquivos PST e com destino no Exchange Online. Ele deverá inventariar, preservar, preparar, transportar, importar, reconciliar e comprovar. O produto não será apenas um uploader, um conjunto de scripts ou uma interface que chama ferramentas externas sem governança.

O resultado de negócio é permitir que uma organização retire o legado com risco controlado, preservando identidade, hierarquia de pastas, metadados, cadeia de custódia, rastreabilidade de exceções e evidência suficiente para auditoria. A plataforma deve continuar operável após interrupções longas, mudança de operador, reinício de worker, expiração de credencial ou reprocessamento controlado.

> [!CAUTION]
> **BLOQUEIO / DECISÃO CRÍTICA**
> **Nenhum desenvolvedor está autorizado a codificar uma forma de contornar quotas, throttling, licenciamento ou interfaces não públicas da Microsoft.** Se o destino não suportar determinado volume ou tipo de dado, o sistema bloqueia o job, registra a causa e exige um caminho aprovado. “Funcionar no laboratório” não torna uma integração suportada.

### 1.1 Definição de “sistema real”

Para este projeto, um sistema real precisa satisfazer simultaneamente:

- infraestrutura reproduzível por código, sem dependência de cliques manuais não documentados;
- autenticação moderna, privilégio mínimo, segregação de funções e rotação de credenciais;
- estado persistente, idempotência, checkpoints, fila durável, dead-letter e retomada;
- processamento de dados não confiáveis em workers isolados e descartáveis;
- trilha de custódia append-only e evidência imutável;
- comportamento determinístico para nomes, partições, destino e replay;
- testes unitários, integração, compatibilidade, performance, chaos e segurança;
- telemetria, alertas, SLO, backup, recuperação de desastre e runbooks operacionais;
- gates externos para qualquer API preview, privada ou contratual;
- documentação que permita a outro time operar e evoluir a plataforma.

### 1.2 O que este produto não será

- Não será automação COM de Outlook executada como serviço.
- Não será um parser PST construído do zero quando há SDKs maduros e licenciáveis.
- Não será um robô de navegador que finge existir uma API Purview.
- Não será uma fila com o PST dentro da mensagem.
- Não será um banco de dados contendo corpo, assunto ou anexos de e-mail sem necessidade formal.
- Não será um sistema que declara sucesso porque o portal mostrou “Complete”.
- Não será uma solução que reabre ou recria partes já importadas e perde a identidade de replay.
- Não usará EWS como fundamento, pois o serviço começa a ser desativado no Exchange Online em outubro de 2026 e a retirada completa está anunciada para abril de 2027.

## 2. Correções obrigatórias ao documento-base

O PDF de origem acertou ao recomendar inspeção, partição, manifestos, quarentena, AzCopy, CSV e reconciliação. Entretanto, quatro hipóteses precisam ser substituídas antes do desenvolvimento.

| **Hipótese anterior** | **Evidência oficial atual** | **Decisão do produto** |
| --- | --- | --- |
| Um PST de 500 GB pode ser dividido e importado ao mesmo archive em ondas sucessivas de aproximadamente 100 GB. | O Purview importa no **main archive mailbox only** e aplica limite de 100 GB; acima disso, a Microsoft orienta abrir suporte. A orientação de troubleshooting afirma que auto-expansion não deve ser usado para o cenário PST/migração. | O adapter Purview bloqueia quando a soma planejada exceder a capacidade suportada ou disponível. Não existe loop automático de cinco ondas. |
| Auto-expanding archive resolve automaticamente a ingestão de 500 GB. | Auto-expansion chega a 1,5 TB para uso normal, mas provisiona capacidade sob demanda e não é um mecanismo de bypass para PST Import. | A feature é inventariada, mas não usada para autorizar import acima de 100 GB. |
| O último passo pode ser automatizado por PowerShell/API pública. | A Microsoft orienta criar novos jobs pela interface do Import Service e informa que PowerShell não é suportado para essa criação. | O adapter Purview é “automação assistida”: gera tudo, acompanha evidências e exige operação humana registrada no portal. |
| Microsoft Graph resolve a ingestão direta de PST no archive. | As APIs de mailbox import/export usam stream FTS opaco; a importação documentada recebe dado exportado por essa família de APIs. A documentação v1.0 e as páginas de archive/preview ainda não são uniformes. | O adapter Graph FTS fica bloqueado por capability gate. Converter PST para FTS só será habilitado após confirmação formal de suporte, teste de fidelidade e API GA para archive. |

> [!WARNING]
> **ATENÇÃO**
> A API Graph de mailbox import/export é estrategicamente importante, mas não aceita um arquivo PST inteiro. Ela cria sessão e recebe itens em FTS base64. Construir um tradutor PST → FTS por conta própria é um produto de protocolo, não uma simples integração. Até a Microsoft confirmar esse uso como suportado, a capacidade permanece `DisabledByPolicy`.

## 3. Matriz de capacidades do produto

Cada destino implementa `ITargetIngestor`. O sistema decide por capability, nunca por `if` espalhado pelo código.

| **Adapter** | **Status inicial** | **Caso suportado** | **Bloqueios obrigatórios** |
| --- | --- | --- | --- |
| `PurviewNetworkUpload` | GA / habilitado | PSTs Unicode, partes recomendadas ≤ 20 GB, até 500 linhas por CSV, destino primário ou main archive, dentro da quota suportada | \>100 GB no main archive; archive desabilitado; TargetRootFolder `/`; arquivo repetido em destino diferente; SAS ausente/expirado |
| `GraphMailboxFts` | bloqueado | Somente após API/tenant/cenário aprovados e stream FTS reconhecido | API preview; ausência de archive ID suportado; PST→FTS não homologado; consentimento insuficiente |
| `ContractualFastIngest` | não instalado | API privada/partner contratada e documentada | Contrato, SDK ou termo de suporte ausente; endpoint não permitido |
| `PrepareOnly` | habilitado | Inspecionar, dividir, validar e emitir pacote de migração | Não declara itens importados; não emite certificado final |

O registry de capacidades deverá persistir `provider`, `apiVersion`, `cloud`, `featureState`, `validatedAt`, `evidenceUri`, `tenantScope` e `approvedBy`. A validade padrão da comprovação é 90 dias; qualquer mudança de API ou documentação força revalidação.

### 3.1 Regras duras do adapter Purview

- tamanho-alvo de parte: 18 GiB; limite operacional configurável até 20 GB;
- nomes únicos e case-sensitive dentro da área Microsoft;
- máximo de 500 linhas por mapping CSV;
- `IsArchive=TRUE` apenas se `ArchiveStatus=Active` e o GUID estiver presente;
- `TargetRootFolder` nunca pode ser `/` em import para archive;
- a identidade de replay é `tenant + mailbox + targetRoot + pstSha256`;
- o mesmo byte stream e o mesmo target root devem ser reutilizados no retry;
- import total planejado para main archive não excede 100 GB nem a capacidade observada;
- novas ondas acima do limite exigem evidência de autorização da Microsoft e adapter específico;
- criação e início do import job no portal são tarefas humanas com quatro-olhos.

### 3.2 Regra para um único PST de 500 GB

O arquivo pode e deve ser inspecionado e dividido em partes seguras. Isso resolve processamento, transporte e recuperação; não cria capacidade no destino. A saída do planejamento será uma destas decisões:

6. `SUPPORTED_PURVIEW`: até 100 GB elegíveis para o main archive e quota disponível.
7. `FILTER_REQUIRED`: conjunto completo excede o suportado, mas uma política aprovada reduz o universo.
8. `MICROSOFT_ASSESSMENT_REQUIRED`: volume acima de 100 GB para o mesmo archive.
9. `ADVANCED_CONNECTOR_REQUIRED`: cenário depende de ingestão por item ou API contratual.
10. `TARGET_REDESIGN_REQUIRED`: o destino licenciado não comporta o dado ou viola o mapeamento 1:1.

> [!CAUTION]
> **BLOQUEIO / DECISÃO CRÍTICA**
> A plataforma jamais redistribuirá silenciosamente os dados de um usuário em archives de outros usuários. O archive é destinado ao próprio usuário ou à própria shared mailbox. Fracionar identidade para “ganhar quota” viola o desenho do serviço.

## 4. Como superar Archive Shuttle sem propaganda vazia

Archive Shuttle declara migração em escala de petabytes, trilha de auditoria, marca digital por item, filtragem, correspondência usuário-asset, workflow modular, preservação de estrutura/metadados, redução de impacto e protocolo de ingestão avançada. Superar o produto significa demonstrar métricas iguais ou melhores em teste independente, não escrever “mais seguro” em apresentação.

| **Dimensão** | **Gate mínimo para paridade** | **Gate para afirmar superioridade** |
| --- | --- | --- |
| Fidelidade | ≥ 99,99% dos itens elegíveis preservados; 100% das exceções explicadas | 100% dos tipos suportados preservados em corpus homologado e desvio material zero |
| Cadeia de custódia | hash de fonte/parte, manifesto assinado, log append-only | fingerprint por item, prova WORM, verificação independente e relatório reprodutível |
| Escala | 100 TB de carteira sem perda de controle | benchmark público/repetível com custo e throughput melhores no mesmo destino |
| Retomada | recuperação após crash sem recomeçar job inteiro | RPO lógico zero para artefatos aprovados e replay sem duplicidade demonstrado |
| Segurança | segredo fora de código, RBAC, private endpoints, hardening | threat model auditado, pen-test independente, SBOM, assinatura e evidência de supply chain |
| Operação | dashboards, DLQ, runbooks, SLO | tempo médio de diagnóstico menor e auto-remediação segura para falhas conhecidas |
| Conectores | EV + PST + Purview | adapters adicionais suportados sem contaminar o domínio |
| Experiência | ondas, aprovações, relatórios | planejamento adaptativo, capacidade preditiva e evidência pronta para auditoria |

### 4.1 KPIs oficiais do produto

- `SourceBytesDiscovered`, `EligibleBytes`, `PreparedBytes`, `ImportedBytes`, `ExceptionBytes`.
- `SourceItems`, `EligibleItems`, `PreparedItems`, `ImportedItems`, `SkippedItems`, `QuarantinedItems`.
- `ItemFidelityPassRate`, `FolderFidelityPassRate`, `MetadataFidelityPassRate`.
- `DuplicateRiskBlockedCount`, `UnsafeReplayBlockedCount`, `QuotaGateBlockedCount`.
- `MeanPrepareThroughputGiBPerHour`, `P95ItemImportLatency`, `WorkerUtilization`.
- `EvidenceCompletenessRate`, que precisa ser 100% para fechar o job.
- `RTOObserved`, `RPOObserved`, `MTTD`, `MTTR`.
- `CostPerPreparedGiB` e `CostPerSuccessfullyImportedGiB`.

## 5. Requisitos funcionais e não funcionais

### 5.1 Requisitos funcionais obrigatórios

| **ID** | **Requisito** | **Critério de aceite resumido** |
| --- | --- | --- |
| RF-001 | Cadastrar tenant, projeto, fonte, dono e destino | Identidade 1:1 validada; nenhum PST órfão entra no pipeline |
| RF-002 | Inventariar Enterprise Vault | Archives, IDs, owners, tamanho e política exportados para manifesto |
| RF-003 | Ingerir PST de UNC, disco ou blob privado | Cópia imutável, SHA-256, tamanho e origem registrados |
| RF-004 | Inspecionar PST sem Outlook | árvore, formatos, contagens, datas, anomalias e risco persistidos |
| RF-005 | Particionar PST grande | partes ≤ política; nome/hash/manifesto determinísticos |
| RF-006 | Validar com engine independente | contagem e estrutura compatíveis ou quarentena explícita |
| RF-007 | Planejar destino e ondas | quotas, licença, limite do adapter e target root verificados |
| RF-008 | Fazer upload Purview por AzCopy | versão suportada, logs sanitizados, hash/resultado persistidos |
| RF-009 | Gerar mapping CSV | schema oficial, case, unicidade e máximo de 500 linhas validados |
| RF-010 | Registrar operação do portal | operador, aprovador, hora, job ID e evidência anexada |
| RF-011 | Reconciliar | fonte × parte × serviço × destino, com desvio classificado |
| RF-012 | Reprocessar com segurança | replay do mesmo artefato e target; regeneração bloqueada após import |
| RF-013 | Gerenciar quarentena | motivo, artefato, proprietário, decisão e SLA |
| RF-014 | Emitir certificado | somente após reconciliação, evidência e aprovação |
| RF-015 | Exportar auditoria | pacote assinado, verificável sem acesso ao banco principal |

### 5.2 Requisitos não funcionais

| **ID** | **Requisito** | **Meta inicial de produção** |
| --- | --- | --- |
| RNF-001 | Disponibilidade do plano de controle | 99,9% mensal |
| RNF-002 | Durabilidade de evidência | armazenamento redundante + WORM; perda tolerada zero |
| RNF-003 | RPO do controle | ≤ 5 min; eventos de custódia já confirmados não podem ser perdidos |
| RNF-004 | RTO do controle | ≤ 4 h |
| RNF-005 | Isolamento | tenant/project em toda chave, policy e caminho; teste automatizado de cross-tenant |
| RNF-006 | Retomada worker | nova instância retoma pelo último checkpoint confirmado |
| RNF-007 | Segurança | zero segredo estático no repositório ou pipeline; identidade gerenciada onde possível |
| RNF-008 | Auditabilidade | 100% das ações privilegiadas com actor, razão, correlação e antes/depois |
| RNF-009 | Performance de preparação | baseline aferida por perfil, sem promessa antes do benchmark do hardware |
| RNF-010 | Acessibilidade | portal operável por teclado e compatível com WCAG 2.2 AA |
| RNF-011 | Observabilidade | logs, métricas e traces correlacionados por `correlationId` e `jobId` |
| RNF-012 | Evolução | adapters sem referência a SDK de fornecedor no domínio |
