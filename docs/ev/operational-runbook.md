# Runbook operacional — conector de exportação EV

Operação do Source Connector com adapters multiversão
([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)). Complementa os
runbooks operacionais gerais do runbook de engenharia (§42).

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
