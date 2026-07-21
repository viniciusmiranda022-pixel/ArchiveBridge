# Catálogo de adapters de destino (Microsoft 365)

Registro dos adapters de destino (`ITargetIngestor`, runbook
[§24](../runbook/04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates))
e do estado de suas capabilities. O objetivo é que o produto tenha
**destinos evoluíveis** e **não fique amarrado a um único adapter**: o
Purview é o caminho GA hoje; o Graph é condicional, com ciclo de promoção
definido.

## Estados

**Status arquitetural do adapter** (ele existe e é selecionável?):

- `ENABLED` — habilitado e selecionável em produção;
- `CONDITIONAL` — presente na arquitetura, selecionável **apenas** quando a
  capability da rota estiver certificada;
- `RETIRED` — removido/descontinuado.

**Estado da capability de uma rota** (ciclo de promoção):

```text
BLOCKED_PENDING_EVIDENCE → CANDIDATE → CERTIFIED → ENABLED
```

Promoção até `ENABLED`, com **todas** as evidências definidas atendidas,
ocorre por **configuração versionada + capability evidence aprovada**. Um
**novo ADR** só é necessário se mudar **contrato, modelo de segurança ou
arquitetura** do adapter — não para uma promoção já prevista.

## Catálogo

| Adapter | Status arquitetural | Rota / capability | Estado da capability | ADR |
| --- | --- | --- | --- | --- |
| `PurviewPstImportAdapter` | **ENABLED** | PST → Purview Network Upload (GA) | `ENABLED` | [ADR-0006](0006-purview-adapter-ga-inicial.md) |
| `GraphFtsTargetAdapter` | **CONDITIONAL** | PST/EV → FTS → Graph (`GraphFtsImportFromPstEv`) | `BLOCKED_PENDING_EVIDENCE` | [ADR-0007](0007-graph-fts-bloqueado.md) |

Notas:

- `PurviewPstImportAdapter = ENABLED` é o adapter GA inicial para PST
  (mantém o capacity gate e o bloqueio >100 GB / `MICROSOFT_ASSESSMENT_REQUIRED`
  do ADR-0006). Não é exclusivo: é o primeiro destino, não o único.
- `GraphFtsTargetAdapter` permanece **CONDITIONAL** — disponível na
  arquitetura, selecionável só quando `GraphFtsImportFromPstEv` for
  promovida a `ENABLED`. O bloqueio é **específico à rota PST/EV → FTS**,
  não ao Graph em geral (ver ADR-0007).
- Rotas do Graph **fora** de PST/EV (ex.: round-trip de dados exportados
  pela própria API Graph) não são objeto do bloqueio deste catálogo e, se
  vierem a ser usadas, entram como capability própria.

## Manutenção

- Mudança de estado de capability (`BLOCKED_PENDING_EVIDENCE` → `CANDIDATE`
  → `CERTIFIED` → `ENABLED`) é registrada aqui, com link para a capability
  evidence e a configuração versionada que a promoveu.
- Adição de novo adapter de destino (ex.: adapter contratual de ingestão
  rápida, runbook §29) entra como nova linha, com seu ADR.
