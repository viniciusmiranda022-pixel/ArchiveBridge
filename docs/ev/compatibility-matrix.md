# Matriz de compatibilidade Enterprise Vault

Estado de suporte por família de versão
([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)). **Regra de
honestidade comercial**: a arquitetura permitir um adapter **não**
significa suporte; suporte só é declarado após laboratório, testes e
certificação do adapter correspondente.

## Critérios de suporte

| Nível | Significado | Pode ser declarado comercialmente? |
| --- | --- | --- |
| **compatível** | a arquitetura comporta um adapter para a família; nenhum teste executado | **Não** — apenas planejamento interno |
| **testado** | adapter implementado e exercitado em laboratório na família, sem certificação completa | Não — pilotos controlados apenas |
| **certificado** | adapter aprovado no plano de testes completo ([test-plan.md](test-plan.md)) na família/build de laboratório | **Sim**, no escopo certificado |
| **não suportado** | sem adapter planejado ou família vetada | Não — somente modo assistido/bloqueio |

## Matriz

Para linhas em `planejado`, as colunas **Adapter** e **Funcionalidades** são
**candidatura/previsão**, não garantia: a família apenas habilita a
avaliação do adapter, e o uso efetivo depende do capability discovery
(presença real das capabilities obrigatórias) e da certificação do build.
**Nenhuma versão é suportada pela string de versão.** As famílias 12.1–15.x
são **candidatas** ao adapter PowerShell nativo.

| Versão EV | Adapter | Nível de automação | Funcionalidades previstas (sujeitas a discovery + certificação) | Limitações conhecidas | Status |
| --- | --- | --- | --- | --- | --- |
| 15.x | EV PowerShell Adapter (candidato) | total previsto | inventário `Get-EVArchive`; exportação `Export-EVArchive` Unicode segmentada (tamanho-alvo `ArchiveBridgeOperationalTargetMb`, validado contra `DetectedMin/MaxPstSizeMb`); retry nativo; relatório — **cada item sujeito a detecção** | snap-in/cmdlet/parâmetro/permissão/Outlook podem variar por build | **planejado** |
| 14.x | EV PowerShell Adapter (candidato) | total previsto | idem 15.x | idem 15.x | **planejado** |
| 13.x | EV PowerShell Adapter (candidato) | total previsto | idem 15.x | idem 15.x | **planejado** |
| 12.1–12.x (≥12.1) | EV PowerShell Adapter (candidato) | total previsto | idem 15.x | idem 15.x | **planejado** |
| 12.0 | EV Legacy Script Adapter (família 12.0) | parcial (script certificado) | inventário e exportação conforme capacidades da família; segmentação a validar | conjunto de cmdlets/relatório difere; requer implementação própria | **planejado** |
| 11.x | EV Legacy Script Adapter (família 11.x) | parcial (script certificado) | a definir em laboratório | exportação PowerShell limitada/ausente; possíveis dependências de Outlook | **planejado** |
| 10.x | EV Legacy Script Adapter (família 10.x) | parcial (script certificado) | a definir em laboratório | idem 11.x, superfícies mais antigas | **planejado** |
| < 10.0 | — | modo assistido | operador executa exportação guiada; produto valida, hash e ingere os PSTs | sem automação; throughput dependente do operador | **não suportado** (assistido) |
| Qualquer versão sem adapter certificado para o build | Assisted Export Adapter | modo assistido | validação, hash, inventário e ingestão dos PSTs exportados manualmente | sem automação de exportação | fail closed: **assistido ou bloqueado** |

Estados possíveis da coluna Status: `planejado` → `em laboratório` →
`certificado`; ou `não suportado`. Promoção de status **somente** com
evidência do plano de testes anexada (PR referenciando os resultados de
laboratório); rebaixamento imediato se regressão for detectada em novo
build.

## Regras de manutenção

1. Uma linha por família; builds testados são registrados na evidência de
   certificação, não na matriz.
2. `Export-EVArchive` presente ≠ família 12.1+: a classificação vem do
   **capability discovery**, nunca da string de versão.
3. A matriz é referenciada pela fábrica de adapters em runtime (dados de
   certificação embarcados e versionados); divergência entre matriz e
   binário publicado é defeito de release.
