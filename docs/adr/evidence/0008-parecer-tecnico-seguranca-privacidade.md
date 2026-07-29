# Parecer Técnico de Segurança e Privacidade — ADR-0008

**Projeto:** ArchiveBridge  
**ADR analisado:** ADR-0008 — Modelo de isolamento por tenant/projeto  
**Branch analisada:** `claude/adr-0008-isolamento-onprem`  
**Head analisado:** `3e3be0c5196e0b6aa68df274847a7408fd190067`  
**Data da análise:** 28 de julho de 2026  
**Decision Owner:** Vinicius Miranda  
**Classificação:** Confidencial — Engenharia, Segurança e Privacidade  
**Natureza:** Parecer técnico preliminar de Segurança e Privacidade; não constitui parecer jurídico nem substitui a indicação formal do encarregado/DPO pela organização.

---

## 1. Objeto e escopo

Este parecer revisa o ADR-0008 e a evidência `0008-threat-model-avaliacao-dados.md`, considerando a baseline vigente do ArchiveBridge:

- produto instalado on-premises;
- Control Plane, SQL Server, workers, segredos, storage e evidências sob controle do cliente;
- Microsoft 365 como destino externo;
- monólito modular com workers isolados;
- fila durável em SQL Server;
- segregação por tenant, projeto, workload e operação;
- nenhuma dependência obrigatória de Azure PaaS;
- scaffolding e código ainda bloqueados.

A análise cobre:

1. isolamento multitenant;
2. identidade e privilégio mínimo;
3. autorização e Row-Level Security;
4. segredos e material criptográfico;
5. rede e segmentação;
6. proteção de artefatos e workers;
7. logs, auditoria e evidências;
8. privacidade, retenção e papéis LGPD;
9. transferência internacional;
10. resposta a incidentes;
11. condições para aceite do ADR;
12. condições obrigatórias antes de produção.

---

## 2. Conclusão executiva

### Resultado

> **APROVAÇÃO TÉCNICA CONDICIONAL**

O desenho do ADR-0008 é **tecnicamente coerente com a baseline on-premises** e apresenta controles apropriados para:

- isolamento por tenant/projeto;
- menor privilégio;
- segregação entre workloads;
- prevenção de acesso cross-tenant;
- custódia local de segredos;
- limitação de egress;
- minimização de dados;
- fail-closed;
- proteção de artefatos e evidências.

Não foi identificado motivo técnico para rejeitar a arquitetura.

Entretanto, o ADR e sua evidência devem registrar as correções obrigatórias deste parecer antes do flip `proposto → aceito`. Os pontos mais relevantes são:

1. **retenção de 7–10 anos não pode ser padrão fixo** sem fundamento contratual/legal;
2. **papéis de controlador, operador e suboperador não podem ser afirmados de forma absoluta** para todos os projetos;
3. **RLS precisa de controles explícitos contra vazamento por pooling/session context**;
4. **o acesso app-only ao Exchange Online deve proibir permissões amplas paralelas**, pois grants podem se somar;
5. **DPAPI deve permanecer restrito ao perfil de nó único**, com escopo e recuperação definidos;
6. **transferência internacional, DPA e localização do tenant precisam de validação por engajamento**;
7. **resposta a incidente deve prever avaliação e escalonamento ao controlador dentro do prazo regulatório aplicável**;
8. o perfil HA de segredos permanece corretamente bloqueado até uma solução concreta ser certificada.

---

## 3. Metodologia

Foram avaliados:

- ADR-0008;
- threat model STRIDE e avaliação de dados anexos ao ADR;
- ADR-0003, ADR-0006 e ADR-0007 como dependências;
- baseline de engenharia segura do ArchiveBridge;
- controles oficiais do SQL Server, Windows Server e Exchange Online;
- requisitos vigentes da LGPD e regulamentações da ANPD sobre encarregado, incidentes e transferência internacional;
- DPA vigente da Microsoft como documento contratual a ser verificado por engajamento.

Escala de risco:

| Nível | Interpretação |
|---|---|
| Crítico | impede aceite e produção |
| Alto | impede produção; pode impedir aceite se não houver contrato claro |
| Médio | exige controle, teste ou decisão documentada |
| Baixo | melhoria recomendada ou requisito operacional |

---

## 4. Avaliação por domínio de controle

### 4.1 Isolamento por tenant e projeto

**Situação:** adequada com condições.

Controles positivos:

- `tenant_id` obrigatório;
- tenant/projeto explícitos nos contratos;
- índices liderados por tenant;
- RLS no SQL Server;
- autorização na Application;
- testes cross-tenant no primeiro PR de scaffolding;
- fail-closed.

#### Condição SEC-01 — contexto de tenant no SQL

A implementação deverá:

- derivar `tenant_id` da identidade/autorização do servidor, nunca de valor confiado enviado pelo cliente;
- configurar o `SESSION_CONTEXT` na abertura lógica de cada unidade de trabalho;
- limpar ou sobrescrever o contexto antes de devolver conexão ao pool;
- impedir alteração do contexto por queries comuns;
- incluir tenant/projeto em chaves, índices, uniques e foreign keys aplicáveis;
- testar reutilização de conexão entre tenants;
- testar `SELECT`, `INSERT`, `UPDATE`, `DELETE`, bulk operations e procedures;
- falhar se qualquer consulta escopada for executada sem tenant válido.

**Classificação:** Alto antes da implementação; mitigado pelo contrato acima.

### 4.2 Identidades de serviço e segregação

**Situação:** adequada.

A separação Control/EV/PST/Upload/Recon/Evidence reduz blast radius. gMSA é apropriada para Windows Services domain-joined e elimina a gestão manual de senha.

#### Condição IAM-01 — escolha entre gMSA e conta virtual

- **gMSA:** baseline quando o workload precisar acessar SQL, SMB/NAS, EV ou outro recurso de rede autenticado.
- **Conta virtual:** permitida apenas para nó único e quando o acesso de rede puder usar a identidade da máquina sem ampliar privilégio.
- Nunca compartilhar a mesma gMSA entre workloads distintos apenas por conveniência.
- Restringir `PrincipalsAllowedToRetrieveManagedPassword` aos hosts autorizados.
- Proibir logon interativo.
- Proibir associação a grupos administrativos amplos.

#### Condição IAM-02 — identidade externa por operação

Manter separadas:

- Precheck M365;
- reconciliação;
- Graph condicional;
- Purview Operator;
- Purview Approver;
- Upload Worker.

A aplicação de precheck/reconciliação não pode receber permissões de escrita quando apenas leitura for necessária.

### 4.3 Exchange Online RBAC for Applications

**Situação:** adequada com correção obrigatória.

O uso de RBAC for Applications com management scope restrito é apropriado.

#### Condição IAM-03 — grants aditivos

Antes de produção:

- garantir que a aplicação escopada por Exchange Online RBAC **não possua simultaneamente permissões amplas equivalentes no Entra ID**;
- inventariar todos os consentimentos de aplicação;
- testar acesso a mailbox dentro e fora do management scope;
- falhar o gate se a aplicação conseguir acessar mailbox fora da onda;
- registrar `appId`, service principal, role assignment, scope e hash da configuração.

**Classificação:** Alto.

### 4.4 Segredos e DPAPI

**Situação:** adequada para nó único; HA corretamente bloqueado.

DPAPI é compatível com nó único porque a proteção normalmente fica associada à mesma conta e ao mesmo computador.

#### Condição SEC-02 — uso correto do DPAPI

- usar escopo da identidade dedicada do serviço;
- não usar `CRYPTPROTECT_LOCAL_MACHINE` como proteção única para segredos de tenant;
- definir backup/recuperação das chaves DPAPI;
- testar restauração e troca controlada do host;
- nunca persistir segredo em texto claro;
- zerar buffers sensíveis quando tecnicamente possível;
- registrar versão, owner, finalidade e expiração do segredo;
- separar segredo por tenant/operação quando aplicável.

#### Condição SEC-03 — HA de segredos

O estado `BLOCKED_PENDING_EVIDENCE` está correto.

Antes de habilitar HA, escolher e certificar uma solução concreta, por exemplo:

- DPAPI-NG com descritor de proteção e governança de AD;
- key ring compartilhado protegido por certificado;
- HSM;
- secrets manager corporativo do cliente.

A seleção exige ADR/evidência específica com:

- modelo de ameaça;
- rotação;
- backup;
- recuperação;
- revogação;
- auditoria;
- disponibilidade;
- segregação por tenant;
- teste de perda de nó.

### 4.5 SAS do Purview

**Situação:** adequada, condicionada ao ADR-0006.

A custódia local, worker dedicado, ausência de log e destruição das cópias locais são controles corretos.

#### Condição SEC-04 — exposição em processo

- command line não pode ser registrada;
- bloquear transcript/history;
- restringir visibilidade de processos;
- desabilitar dumps automáticos do processo contendo argumentos, salvo procedimento de incidente controlado;
- sanitizar stdout/stderr;
- usar timeout;
- destruir arquivo/objeto local com SAS;
- testar vazamento em logs, eventos e pacote de evidência.

### 4.6 Rede

**Situação:** adequada.

Princípios aprovados:

- nenhuma entrada originada da internet;
- egress externo HTTPS 443 somente aos endpoints Microsoft autorizados;
- fluxos internos explicitados na matriz;
- segmentação firewall/VLAN;
- negação de tráfego lateral desnecessário.

#### Condição NET-01 — matriz verificável

Para cada implantação, registrar:

- origem;
- destino;
- protocolo;
- porta;
- direção;
- DNS/FQDN;
- identidade;
- finalidade;
- owner;
- justificativa;
- ambiente;
- evidência do teste;
- regra de expiração/revisão.

Não liberar wildcard amplo quando houver alternativa suportada.

### 4.7 Storage, filesystem e evidências

**Situação:** adequada com correção de retenção.

Controles positivos:

- containers por ciclo de vida;
- ACL exclusiva;
- criptografia at rest;
- WORM para evidências;
- imutabilidade;
- path canonicalization;
- no-execute em diretórios de dados;
- quarantine.

#### Condição PRIV-01 — retenção

Remover a frase genérica:

> `evidence WORM 7–10 anos ou política`

Substituir por:

> A retenção de evidências, relatórios, logs e artefatos é definida por política versionada por engajamento, com fundamento legal, contratual, regulatório e operacional documentado pelo controlador. O prazo deve ser o mínimo necessário. Qualquer prazo de 7–10 anos é perfil específico, nunca padrão universal.

A política deve diferenciar:

- PST original;
- parts;
- scratch;
- quarantine;
- logs;
- mapping CSV;
- manifestos;
- relatórios;
- evidência WORM;
- registros de incidente;
- backups.

**Classificação:** Alto para privacidade.

### 4.8 Logs e minimização

**Situação:** adequada.

A proibição de subject/body/anexo/SAS/token/path real e o HMAC por tenant são coerentes.

#### Condição PRIV-02 — pseudonimização

- HMAC não é anonimização;
- os dados continuam pessoais quando houver possibilidade razoável de associação;
- separar chaves HMAC por tenant;
- versionar `keyVersion`;
- limitar acesso à tabela de resolução;
- não incluir identificadores reversíveis em telemetria central sem necessidade.

### 4.9 Papéis LGPD

**Situação:** precisa de correção textual.

A afirmação “o produto não é controlador; opera sob instrução do cliente” deve ser tratada como hipótese de contratação, não verdade universal.

#### Condição PRIV-03 — papéis por engajamento

Substituir por:

> No modelo contratual esperado, o cliente tende a atuar como controlador, a TISCO/operador da migração como operadora e a Microsoft como suboperadora/provedora do serviço de destino, conforme contratos e instruções documentadas. Os papéis efetivos devem ser confirmados por engajamento; o software, isoladamente, não determina a qualificação jurídica.

Registrar:

- controlador;
- operador;
- suboperadores;
- instruções;
- finalidades;
- categorias de dados;
- titulares;
- localização;
- retenção;
- subprocessadores;
- canal de incidente;
- atendimento de direitos;
- retorno/eliminação ao encerrar o serviço.

### 4.10 Transferência internacional e Microsoft 365

**Situação:** adequada como pendência por engajamento.

A migração pode envolver transferência internacional ou acesso transfronteiriço, conforme a configuração e localização do tenant e dos serviços.

#### Condição PRIV-04 — checklist por projeto

Antes da execução:

- identificar localização do tenant e workloads;
- revisar o DPA vigente da Microsoft;
- identificar mecanismo aplicável da Resolução CD/ANPD nº 19/2024;
- verificar cláusulas-padrão, equivalentes, específicas, normas corporativas globais ou decisão de adequação, conforme o caso;
- registrar suboperadores;
- documentar base legal do tratamento;
- documentar instrução do controlador;
- registrar risco residual de soberania/jurisdição;
- anexar aprovação do responsável de privacidade do cliente.

### 4.11 Resposta a incidentes

**Situação:** parcialmente descrita; precisa de requisito operacional explícito.

#### Condição IR-01 — escalonamento LGPD

O ArchiveBridge deve:

- detectar e preservar evidências;
- classificar impacto;
- identificar categorias de dados e titulares;
- notificar imediatamente o controlador;
- fornecer informações suficientes para decisão regulatória;
- manter registro do incidente pelo prazo aplicável;
- suportar comunicação em fases quando informações ainda estiverem incompletas;
- nunca comunicar diretamente à ANPD/titular em nome do controlador sem mandato contratual.

O controlador deve conseguir avaliar e, quando aplicável, comunicar ANPD e titulares dentro do prazo regulatório vigente.

### 4.12 Threat model

**Situação:** bom, mas deve evoluir por slice.

O STRIDE atual cobre os ativos principais. No primeiro scaffolding, adicionar testes para:

- session-context/pooling;
- cross-tenant via IDs previsíveis;
- confused deputy;
- tenant spoofing;
- broad app consent;
- gMSA host não autorizado;
- restore de segredo em host errado;
- SAS em process list/dump;
- symlink/reparse point;
- malicious PST resource exhaustion;
- replay de evento;
- operação ambígua;
- WORM overwrite;
- log canary;
- assinatura inválida de evidência.

---

## 5. Registro consolidado de riscos

| ID | Risco | Prob. | Impacto | Nível | Tratamento |
|---|---|---:|---:|---|---|
| R-01 | Vazamento cross-tenant por falha de query/session context | Média | Crítico | Alto | Application auth + RLS + pool tests |
| R-02 | Aplicação EXO com consentimento amplo paralelo ao RBAC | Média | Crítico | Alto | inventário de grants + negative tests |
| R-03 | Segredo DPAPI indisponível após troca/perda de host | Média | Alto | Alto | backup/recovery test; nó único |
| R-04 | HA habilitado sem secrets store certificado | Média | Crítico | Alto | manter `BLOCKED_PENDING_EVIDENCE` |
| R-05 | SAS exposto em argv, log ou dump | Média | Crítico | Alto | worker dedicado + sanitização + leak test |
| R-06 | Retenção excessiva de conteúdo/evidência | Média | Alto | Alto | política por engajamento; mínimo necessário |
| R-07 | Papéis LGPD definidos incorretamente | Média | Alto | Alto | matriz contratual por engajamento |
| R-08 | Transferência internacional sem mecanismo documentado | Média | Alto | Alto | DPA + Res. 19/2024 checklist |
| R-09 | gMSA usada por hosts/workloads indevidos | Baixa/Média | Alto | Médio | restrição de retrieval + auditoria |
| R-10 | Incidente não escalado em tempo hábil ao controlador | Média | Alto | Alto | runbook IR + SLA contratual |
| R-11 | Artefato alterado após validação | Baixa | Crítico | Médio | SHA-256 + immutability + WORM |
| R-12 | Conteúdo PST hostil causar exaustão | Média | Alto | Médio | limites, timeout, quota, worker isolado |

---

## 6. Condições para aceitar o ADR-0008

O ADR pode ser aceito após incorporar ou registrar formalmente:

1. **PRIV-01:** retenção por política/engajamento, sem prazo universal de 7–10 anos;
2. **PRIV-03:** papéis LGPD como hipótese contratual a confirmar por projeto;
3. **SEC-01:** contrato para `SESSION_CONTEXT`, pooling e testes cross-tenant;
4. **IAM-03:** proibição/teste de grants amplos paralelos ao EXO RBAC;
5. **SEC-02:** DPAPI em escopo de serviço/nó único com recuperação;
6. **PRIV-04:** checklist de transferência internacional e DPA;
7. **IR-01:** escalonamento de incidente ao controlador;
8. manutenção do perfil HA de segredos como `BLOCKED_PENDING_EVIDENCE`.

Essas condições não exigem código agora; devem ser registradas como:

- correções do ADR/evidência; e
- condições obrigatórias do primeiro scaffolding e da promoção a produção.

---

## 7. Condições obrigatórias no primeiro PR de scaffolding

O primeiro PR com persistência multitenant deve incluir:

- architecture tests;
- tenant/project em contratos;
- RLS;
- autorização na Application;
- connection-pool isolation tests;
- cross-tenant negative tests;
- migration SQL com security policy;
- unique constraints escopadas;
- redaction canaries;
- gMSA/identity configuration contract;
- secrets abstraction;
- DPAPI profile single-node;
- HA secrets feature disabled;
- audit events;
- incident event schema;
- no code path that accepts tenant solely from request input.

---

## 8. Condições obrigatórias antes de produção

- penetration test de autorização;
- testes cross-tenant completos;
- teste de restore DPAPI;
- validação de gMSA host retrieval;
- consent inventory M365;
- negative test fora do EXO management scope;
- SAS leak test;
- matriz de fluxos aprovada;
- filesystem/reparse point tests;
- WORM restore/verification;
- backup/restore;
- incident tabletop;
- DPA e transferência internacional validados;
- política de retenção aprovada;
- mecanismo HA de segredos certificado, caso HA seja habilitado;
- revisão de risco residual assinada.

---

## 9. Parecer proposto para registrar no gate

### Parecer de Segurança

> O modelo de isolamento do ADR-0008 foi revisado sob a ótica de segurança de aplicação, identidade, rede, persistência, segredos, workers e cadeia de custódia. O desenho on-premises é tecnicamente adequado e preserva os objetivos de controle do runbook. Recomenda-se **aprovação técnica condicional**, sujeita às condições SEC-01 a SEC-04, IAM-01 a IAM-03, NET-01 e IR-01 deste parecer. O perfil HA de segredos permanece `BLOCKED_PENDING_EVIDENCE`. Nenhum controle descrito dispensa testes de implementação, autorização cross-tenant, análise de grants e validação operacional antes de produção.

### Parecer de Privacidade

> A avaliação de dados do ADR-0008 foi revisada sob a ótica de minimização, retenção, papéis de tratamento, transferência internacional, contratos e resposta a incidentes. Recomenda-se **aprovação técnica condicional**, sujeita às condições PRIV-01 a PRIV-04. O prazo de retenção deve ser definido por engajamento e pelo mínimo necessário; os papéis de controlador/operador/suboperador devem ser confirmados contratualmente; e o uso do Microsoft 365 deve ter DPA, localização e mecanismo de transferência internacional avaliados por projeto.

### Recomendação ao Decision Owner

> Após registrar as condições acima no ADR/evidência, o Decision Owner pode autorizar o flip do ADR-0008 de `proposto` para `aceito`, mantendo as condições de implementação e produção como gates verificáveis. A aceitação do ADR não equivale à autorização de produção.

---

## 10. Assinaturas

- **Revisão técnica de Segurança:** análise preliminar produzida com assistência de IA, sob revisão do Decision Owner.
- **Revisão técnica de Privacidade:** análise preliminar produzida com assistência de IA, sem natureza de parecer jurídico.
- **Responsável institucional de Segurança:** __________________________________
- **Encarregado/DPO ou responsável de Privacidade:** __________________________
- **Decision Owner — Vinicius Miranda:** _____________________________________
- **Data:** ____/____/________
- **Ressalvas adicionais:** __________________________________________________

---

## 11. Referências oficiais consultadas

- Lei nº 13.709/2018 — Lei Geral de Proteção de Dados Pessoais.
- Resolução CD/ANPD nº 15/2024 — Comunicação de Incidente de Segurança.
- Resolução CD/ANPD nº 18/2024 — Atuação do Encarregado.
- Resolução CD/ANPD nº 19/2024 — Transferência Internacional de Dados.
- Microsoft Products and Services Data Protection Addendum — edição vigente na data do engajamento.
- Microsoft Learn — SQL Server Row-Level Security.
- Microsoft Learn — Group Managed Service Accounts.
- Microsoft Learn — DPAPI / CryptProtectData.
- Microsoft Learn — Exchange Online RBAC for Applications.

---

## 12. Controle de versão

| Versão | Data | Alteração |
|---|---|---|
| 1.0 | 28/07/2026 | Parecer técnico preliminar inicial |
