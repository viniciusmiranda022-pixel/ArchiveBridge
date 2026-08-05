# Threat model — delta da Vertical Slice 3 (Enterprise Vault Capability Discovery)

Delta de modelagem de ameaças (STRIDE) para a Vertical Slice 3: descoberta **read-only** das
capacidades técnicas de um ambiente Enterprise Vault antes de qualquer exportação. Estende os threat
models das Slices 1–2. **Não** executa exportação, **não** cria/lê/transfere PST, **não** modifica
nada no Enterprise Vault, e **não** integra Purview/Graph/M365/AzCopy/libpff.

- **Tipo:** threat model (STRIDE) — delta da Slice 3
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge
- **Revisor necessário:** Segurança/Privacidade (DPO)
- **Estado da revisão:** **pendente** — sustenta o desenho da Slice 3; **não** é a aceitação formal
  (ato do Decision Owner) nem substitui a revisão de Segurança/DPO.

> [!NOTE]
> A descoberta é **read-only** e roda com **privilégio mínimo** (nunca exige Domain Admin). O SQL
> guarda **apenas metadados** (versão, status, hashes, adapter, caminho lógico, timestamps). A
> evidência detalhada é um artefato imutável fora do SQL, versionado em filesystem on-premises. Essa
> evidência **pode conter nomes técnicos de servidores/sites/componentes** — é tratada como
> **informação sensível de infraestrutura**: a raiz exige ACL restritiva e nomes técnicos **nunca**
> aparecem em logs.

## 1. Ativos críticos da slice

- Identidade observada do ambiente (`ev_environments`) e a versão observada (evidência, não decisão).
- Conjunto de capacidades descobertas (`ev_capabilities`) e seus hashes de configuração/evidência.
- Assinatura NORMALIZADA do cmdlet de exportação observado (forma, não execução).
- Avaliações de adapter (`ev_adapter_evaluations`) e o adapter selecionado (determinístico, registrado).
- Evidência imutável de descoberta (artefato JSON + `evidence.sha256` + manifesto, por versão).
- Contexto versionado do comando durável (`ev_discovery_commands`).

## 2. Ameaças e mitigações (delta STRIDE)

| Ameaça | Vetor concreto | Mitigação (código) | Mitigação (banco/host) |
| --- | --- | --- | --- |
| **PowerShell injection** | valor de argumento carrega `;`/`\|`/`` ` ``/`$(` para injetar comando | `EvProbeExecutor` valida valores (recusa metacaracteres) ANTES de chamar o host e liga argumentos TIPADOS por variável de ambiente `EV_PROBE_ARG_*` (sem concatenação em script) | — |
| **Execução de script arbitrário** | chamador fornece script livre | a porta só aceita uma SONDA TIPADA (`EvPowerShellProbeKind`) mapeada a um script interno FIXO, imutável e versionado; nunca `CommandName` livre nem script do chamador | — |
| **`Invoke-Expression`** | usar `iex` para avaliar string | recusado explicitamente ANTES de qualquer execução (além da validação de argumentos) | — |
| **Sonda desconhecida** | forjar uma operação fora do catálogo | só as 9 sondas tipadas têm script interno; operação desconhecida ⇒ `ArgumentOutOfRangeException` (fail-closed, host nunca chamado) | — |
| **Module/DLL/PATH hijacking** | carregar módulo/assembly malicioso do PATH | working directory controlado; PATH restrito ao diretório do executável; execução não interativa (`-NoProfile -NonInteractive`); host isolado (Windows Worker) | política de execution host explícita (produção) |
| **Privilégio excessivo** | exigir Domain Admin para descobrir | descoberta read-only com permissões MÍNIMAS documentadas; nada além de leitura | conta de serviço de privilégio mínimo (lab-pending) |
| **Credencial em linha de comando/log** | senha/token em argumento ou stdout | argumentos ligados por variável de ambiente (nunca na linha de comando); a saída é um envelope JSON fixo (sem eco de segredo); `stderr` não vazio ⇒ fail-closed; nada de credencial persistida | — |
| **Vazamento de servidor/site/archive em log** | logar nomes técnicos de infraestrutura | mensagens usam códigos/contagens; nomes técnicos ficam só na evidência sob ACL, nunca em log | ACL restritiva na raiz de evidência |
| **Output excessivo (DoS de memória)** | cmdlet devolve saída gigante | limite em BYTES reais (UTF-8) por stream, aplicado durante a leitura (nunca aloca além do teto); processo morto ao exceder; `OutputLimitExceeded` ⇒ fail-closed | — |
| **Timeout / cmdlet travado** | sonda pendura o worker | timeout OBRIGATÓRIO por requisição (CTS); estouro ⇒ processo morto e `TimedOut` fail-closed | heartbeat periódico do Job |
| **Saída malformada** | JSON inválido para forjar capacidade | envelope VERSIONADO validado propriedade a propriedade (schema/probe/success/data) — nada ausente vira `false/0`; falha ⇒ `EV_DISCOVERY_OUTPUT_INVALID` (fail-closed) | — |
| **Capacidade declarada sem evidência** | inferir suporte pela versão textual | uma capacidade só é `Available` com EVIDÊNCIA positiva; ausência ⇒ `Indeterminate`/`Unavailable` (fail-closed) | `CK_evcap_availability` |
| **Spoofing de versão** | ambiente reporta versão falsa para escolher adapter | seleção é por CAPACIDADES (assinatura observada), nunca pela versão textual | — |
| **Downgrade de adapter** | forçar um adapter mais permissivo | política determinística por precedência; empate entre compatíveis ⇒ `EV_ADAPTER_AMBIGUOUS` (fail-closed) | `ev_adapter_evaluations` (auditável) |
| **Seleção incorreta/ambígua de adapter** | duas versões de adapter reivindicam o ambiente | precedência única vence e é REGISTRADA; empate falha fechado; nenhum adapter ⇒ `EV_ADAPTER_NOT_FOUND` | — |
| **Adulteração da evidência** | editar `evidence.json`/`sha256`/manifesto publicado | leitura valida o CONJUNTO (3 arquivos e só eles, hash, sha256, manifesto vs. escopo/versão/tamanho) — falha fechada | — |
| **Sobrescrita silenciosa de evidência** | republicar por cima de uma versão | publicação imutável (rename atômico); republicação valida bundle completo, divergência recusada | `UX_evd_single_current` |
| **I/O de filesystem sob lock SQL** | publicar evidência dentro da transação (SMB/NAS) | protocolo em DUAS transações: reserva (Pending, sem I/O) → publica FORA do SQL → finaliza | status `Pending`; sem `UPDLOCK` durante I/O |
| **Replay de descoberta antiga** | consumir uma versão substituída | só uma versão corrente por ambiente; a anterior vira `Superseded` após a nova finalizar | `UX_evd_single_current` (status 2..5) |
| **Queda entre fases** | worker cai após reservar/publicar | reconciliação idempotente pelos TRÊS hashes (configuração + evidência semântica + conteúdo); recupera a reserva sem versão indevida | `UX_evd_pending_fingerprint` |
| **Reserva pendente concorrente duplicada** | dois workers reservam a MESMA evidência ao mesmo tempo | verificação da pendente + inserção ATÔMICAS na mesma transação sob lock; colisão reconciliada relendo a pendente | índice único filtrado `UX_evd_pending_fingerprint` (status Pending) |
| **Worker defasado persiste evidência** | dono perde o lease, grava | efeitos CERCADOS (época/dono/Processing/lease válido) na MESMA transação + revalidação; senão `FENCED_OUT` | guarda `50030` |
| **Comando obsoleto** | executar comando de uma config de projeto antiga | contexto VINCULADO à versão/hash de configuração e à política; divergência ⇒ `STALE_COMMAND_CONTEXT` | `NOT NULL`/`CHECK` em `ev_discovery_commands` |
| **Job de EV sequestrado (workload genérico)** | reivindicar Job de controle como se fosse descoberta | claim EXCLUSIVO por workload **EnterpriseVault** + `EXISTS(ev_discovery_commands)` | FK composta ao Job; filtro por workload |
| **Cross-tenant / cross-project** | ler/gravar descoberta de outro tenant/projeto | todo store exige `TenantScope`; filtro explícito por `project_id`; FKs compostas | RLS por `SESSION_CONTEXT` em todas as tabelas base |
| **Execução de exportação (fora de escopo)** | descoberta dispara `Export-EVArchive` | a descoberta apenas DESCOBRE o comando (`Get-Command`); nunca o executa; sem porta de exportação nesta slice | — |

## 3. Higiene de logs (obrigatória)

Os logs desta slice **não** podem conter: nomes técnicos de servidores/sites/vault stores/archives;
caminhos reais; conteúdo de mensagem/PST; tokens/SAS/segredos; saída bruta de PowerShell não
sanitizada. A correlação usa `correlation_id`; o detalhe fica na evidência sob ACL, acessível apenas ao
tenant. A saída da sonda é um envelope JSON versionado, validado fail-closed e limitado em BYTES reais;
os argumentos são ligados por variável de ambiente, nunca na linha de comando.

## 4. Fora de escopo (reafirmado)

Sem exportação real, sem `Export-EVArchive` executado, sem PST (criação/leitura/transferência), sem
AzCopy, Purview, Microsoft Graph, Microsoft 365, Exchange Online, libpff, reconciliação, portal ou
migração ponta a ponta. A descoberta é read-only; nenhum dado ou configuração do Enterprise Vault é
modificado. A execução real da exportação e seus segredos são de slices futuras, sob nova evidência e
decisão do Decision Owner.
