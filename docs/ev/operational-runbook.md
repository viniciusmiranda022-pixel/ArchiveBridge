# Runbook operacional — conector de exportação EV

Operação do Source Connector com adapters multiversão
([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)). Complementa os
runbooks operacionais gerais do runbook de engenharia (§42).

> [!NOTE]
> **Status de implementação (2026-08).** Este documento descreve o desenho ALVO completo do conector de
> exportação. O que está REALMENTE implementado hoje: enrollment/identidade/capability handshake/inventário
> (Slice 4C Passo 1, AB-4C-001), a fundação de EXECUÇÃO — comando `Export-EVArchive` construído por API
> segura (sem command injection), throttling exclusivo por connector/archive, idempotência por identidade
> canônica do pedido, captura estruturada de resultado, manifesto canônico com hash/tamanho por output,
> revalidação de replay fail-closed e classificação de itens oversized (Slice 4C Passo 2, AB-4C-005) — e a
> fundação de DELTA STRATEGY & FREEZE PLANNING: seleção determinística de strategy por versão (fail-closed
> para versão/schema desconhecido, nunca `ReceivedDate` isolado), watermark opaco versionado com lineage,
> fases Baseline/Delta/FinalDelta correlacionadas ao export foundation do Passo 2, e planejamento/
> autorização FORMAL de freeze/cutover como estado — nunca execução real (Slice 4C Passo 3, AB-4C-008;
> delta de ameaças em [`threat-model-slice-04c.md`](../security/threat-model-slice-04c.md)). O que
> permanece FORA de escopo (STOP-THE-LINE, nenhum código no repositório): execução contra um Enterprise
> Vault real de cliente, automação Outlook/COM, NATIVE/EML real, freeze/cutover REAL (mudança de acesso,
> retention/policy), descomissionamento EV, AzCopy/Azure staging/SAS, Purview/Graph/Exchange Online/import
> job e reconciliação M365 — as seções abaixo (modo assistido, `ExportRequestId` operacional completo,
> `GetProgress`, retry automático de onda) descrevem esse alvo e ainda não correspondem a código executável.

## Delta incremental e freeze/cutover (Slice 4C Passo 3)

Fundação apenas — nenhuma chamada real ao Enterprise Vault para pull de delta e nenhum comando de freeze
real existem no código deste Passo (STOP-THE-LINE).

1. **Baseline**: primeira carga completa de um archive; estabelece o primeiro watermark canônico
   (`EvDeltaPhase.Baseline`). Exige a mesma capability EV certificada do Passo 2.
2. **Delta**: incremental subsequente, sempre a partir do último watermark canônico aceito
   (`EvDeltaPhase.Delta`); um watermark de outro tenant/projeto/connector/archive, de outra strategy, com
   downgrade de versão ou stale é recusado fail-closed — nunca `ReceivedDate` isolado como único critério
   (§16.5).
3. **Freeze**: solicitado (`FreezeRequired`) e autorizado FORMALMENTE por operador/role competente
   (`FreezeAuthorized`, com justificativa e correlação persistidas) antes de qualquer delta final — apenas
   ESTADO, nunca uma ação real de congelamento de ingestão/shortcut no EV.
4. **FinalDelta**: só elegível com `FreezeAuthorized` já persistido; ao concluir, marca o plano
   `FinalDeltaReady`.
5. **Cutover/rollback retention**: confirmação de cutover avança o plano para
   `RollbackRetentionRequired` — apenas registro de estado, nunca a troca de acesso real do usuário.
6. **Descomissionamento**: permanece SEMPRE `DecommissionBlocked` neste Passo — não há caminho de código
   que libere descomissionamento sem sign-off/retenção/reconciliação de um Passo POSTERIOR.

A delta strategy concreta (`EV-COMPOSITE-WATERMARK@v1`, [`compatibility-matrix.md`](compatibility-matrix.md))
é `Compatible` para as mesmas famílias candidatas do adapter de export — nenhuma certificada ainda; a
emissão real do watermark contra um host EV (via o mesmo mecanismo `Export-EVArchive` do Passo 2, com o
filtro incremental aprovado) é trabalho de um Passo posterior de certificação.

## Instalação

1. Instalar o Source Connector no host indicado do ambiente do cliente
   (outbound-only, §15; enrollment por certificado/mTLS).
2. Conta de serviço com permissões mínimas no EV (inventário + export;
   nunca administrador do domínio).
3. Verificar pré-requisitos da família: snap-in/módulo EV; Outlook quando
   exigido (bitness compatível); disco de staging dimensionado
   (≥ tamanho estimado da maior onda + margem).
4. Executar o **capability discovery**
   ([capability-discovery.md](capability-discovery.md)) e anexar o
   relatório ao projeto. Sem relatório válido, nenhuma exportação inicia.

## Execução de uma onda de exportação

1. Control Plane emite `EvExportRequest` (archives aprovados, tamanho de
   segmento, `ExportRequestId`).
2. Conector resolve o adapter pelo relatório de discovery + matriz de
   certificação; adapter não certificado ⇒ modo assistido ou bloqueio
   (`EV_ADAPTER_UNRESOLVED`) — nunca fallback silencioso.
3. `ValidatePrerequisites` → qualquer reprovação bloqueia com código
   específico (`EV_PREREQ_FAILED`) e item apontado.
4. `StartExport` → acompanhar por `GetProgress`; o operador acompanha
   pelo Portal (progresso normalizado, sem conteúdo).
5. Ao concluir: `ReadExportReport` + `InventoryOutput` → PSTs Unicode com
   SHA-256 e vínculo ao archive; divergência relatório×inventário
   bloqueia (`EV_OUTPUT_INCONSISTENT`).
6. Validação, hash e ingestão seguem o fluxo padrão do produto
   (Parte III do runbook) rumo ao upload M365.

## Retry

- Falha transitória (`EV_EXPORT_TRANSIENT`): retry automático com
  backoff, mesmo `ExportRequestId` — idempotente por contrato.
- Falha após N tentativas: onda fica `FAILED`; operador decide
  `RetryAsync` manual pelo Portal. O retry **preserva o conjunto
  aprovado** — partes já validadas não são regeradas nem duplicadas.
- Interrupção do conector/worker: ao reiniciar, o handle é recuperado e o
  progresso retomado; jamais iniciar segunda exportação para o mesmo
  request (T8 do [plano de testes](test-plan.md)).

## Troubleshooting

| Sintoma | Provável causa | Ação |
| --- | --- | --- |
| `EV_ADAPTER_UNRESOLVED` | build sem adapter certificado ou discovery vencido | reexecutar discovery; conferir matriz; decidir assistido × bloqueio com o aprovador |
| `EV_PREREQ_FAILED` (Outlook) | família exige Outlook ausente/bitness errado | instalar/ajustar conforme pré-requisito da família; reexecutar precheck |
| `EV_PREREQ_FAILED` (permissões) | conta de serviço sem acesso ao Vault Store | corrigir permissão mínima documentada; nunca elevar para admin genérico |
| Exportação lenta / paradas | contenção no EV, disco de staging, antivírus | verificar exclusões e IOPS; consultar exceções do exporter (§16.4) |
| `EV_REPORT_UNAVAILABLE` | relatório não gerado/ilegível na família | tratar exportação como não evidenciada; reprovar e investigar antes de retry |
| `EV_OUTPUT_INCONSISTENT` | inventário ≠ relatório | bloquear onda; investigação obrigatória; jamais aprovar manualmente sem disposição formal |
| Itens problemáticos recorrentes | archive com corrupção/itens não exportáveis | contabilizar via relatório; disposition conforme §22/§23 (nunca ignorar silenciosamente) |
| Suspeita de log com dado sensível | falha de sanitização | seguir runbook 42.6 (segredos em log): conter, rotacionar, limpar |

## Modo assistido

Quando ativo: o Portal emite o roteiro passo a passo da exportação nativa
para o operador do cliente; ao término, o conector valida, calcula hash,
inventaria e ingere os PSTs — mesma custódia e evidência do fluxo
automatizado, automação zero. Registrar no projeto que a onda foi
assistida (nível de automação fica na evidência).
