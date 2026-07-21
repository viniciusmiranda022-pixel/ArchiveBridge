# Plano de testes — exportação EV multiversão

Cobre laboratório, casos de teste, testes de contrato e critérios de
aceite ([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)). Certificação
de adapter por família **somente** com este plano integralmente executado
e evidenciado.

## Laboratório

Ambientes representativos por família de versão, isolados e sem dados
reais de clientes:

| Ambiente | Conteúdo |
| --- | --- |
| LAB-EV-15x / 14x / 13x / 12.1+ | EV da família instalado, Vault Store sintético com archives de teste (pequeno, grande, itens problemáticos) |
| LAB-EV-120 / 11x / 10x | idem, para os adapters legados (um por família implementada) |
| LAB-ASSISTED | qualquer família, sem adapter certificado carregado — valida o fluxo assistido |

Requisitos comuns: conta de serviço com permissões mínimas documentadas;
Outlook quando a família exigir; corpus de archives sintético versionado
(gerador + seeds); snapshot para reexecução limpa.

## Casos de teste (por família/adapter)

| # | Caso | Resultado esperado |
| --- | --- | --- |
| T1 | Descoberta da versão e capacidades | relatório de discovery correto e assinado; família classificada sem usar string de versão isolada |
| T2 | Ausência de cmdlets (snap-in removido) | capability `UNSUPPORTED`; seleção cai para legado/assistido; `EV_ADAPTER_UNRESOLVED` quando aplicável |
| T3 | Exportação de archive pequeno | PSTs Unicode segmentados; contagens = relatório; SHA-256 e vínculo ao archive |
| T4 | Exportação de archive grande (múltiplas partes) | todas as partes ≤ tamanho-alvo; soma de itens = relatório; nenhuma parte corrompida |
| T5 | Segmentação dos PSTs | `DetectedMin/MaxPstSizeMb` medidos no build; `ArchiveBridgeOperationalTargetMb` (18432) validado dentro do intervalo detectado; configuração fora do intervalo é **rejeitada**; partes respeitam o alvo | 
| T6 | Falha parcial (interrupção do serviço EV no meio) | estado consistente; partes incompletas marcadas, nunca aprovadas |
| T7 | Retry após falha | conclui sem duplicar o conjunto aprovado; `retryDuplicates == 0` |
| T8 | Interrupção do worker/conector | retomada segura; nenhuma exportação duplicada (idempotência por `ExportRequestId`) |
| T9 | Archive com itens problemáticos | exceções do exporter (§16.4) capturadas e normalizadas; itens problemáticos contabilizados, não silenciados |
| T10 | Validação dos relatórios | parser da família produz `EvExportReport` fiel ao relatório bruto; `EV_REPORT_UNAVAILABLE` quando ausente |
| T11 | Execução em versões diferentes do EV | mesma suíte, mesmos comportamentos observáveis em cada família certificada |

## Testes de contrato

A **mesma suíte** roda contra todos os adapters (PowerShell, cada legado,
assistido) via `IEvExportAdapter`:

- todas as operações respondem com os tipos e códigos da taxonomia comum;
- idempotência de `StartExportAsync` por `ExportRequestId`;
- `RetryAsync` preserva o conjunto aprovado;
- progresso monotônico e estados terminais consistentes
  (`COMPLETED`/`FAILED`/`CANCELLED`);
- nenhum output com assunto, corpo, credencial ou dado sensível
  (verificação automática de logs/saídas);
- adapter desconhecido/não assinado/não certificado não carrega.

## Critérios de aceite

1. Nenhum adapter é selecionado somente pelo número da versão;
2. Capability discovery é obrigatório e precede qualquer seleção;
3. Adapter desconhecido falha de forma controlada (`EV_ADAPTER_UNRESOLVED`);
4. Exportação é idempotente (`ExportRequestId`);
5. Retry não duplica o conjunto aprovado;
6. Cada PST possui SHA-256 e vínculo com o archive original;
7. Scripts legados são assinados e versionados (recusa sem assinatura);
8. Logs não expõem assuntos, corpos, credenciais ou outros dados
   sensíveis (asserção automática na suíte).

Certificação de uma família = T1–T11 + testes de contrato verdes no
laboratório da família, evidência anexada ao PR que promove o status na
[matriz](compatibility-matrix.md).
