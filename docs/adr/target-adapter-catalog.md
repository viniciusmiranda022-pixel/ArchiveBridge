# Catálogo de adapters de destino (Microsoft 365)

Registro dos adapters de destino (`ITargetIngestor`, runbook
[§24](../runbook/04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates))
e do estado de suas capabilities. O objetivo é que o produto tenha
**destinos evoluíveis** e **não fique amarrado a um único adapter**: o
Purview é o caminho GA hoje; o Graph é condicional, com ciclo de promoção
definido.

## Quatro dimensões separadas

Para **não confundir decisão arquitetural pretendida com estado real de
implementação**, cada adapter é descrito por quatro dimensões
independentes:

1. **Papel arquitetural** — o que o adapter é na arquitetura:
   - `PRIMARY_GA_TARGET` — destino GA primário planejado;
   - `CONDITIONAL` — presente na arquitetura, selecionável **apenas** quando
     a capability da rota estiver certificada;
   - `RETIRED` — removido/descontinuado.
2. **Implementação atual** — existe código/runtime? `NOT_IMPLEMENTED` |
   `IN_PROGRESS` | `IMPLEMENTED`.
3. **Gate atual** — o que trava a habilitação hoje (ADR pendente, evidência
   pendente…).
4. **Estado-alvo** — para onde a decisão aponta quando implementação e gate
   estiverem satisfeitos.

**Nenhum adapter está habilitado em produção hoje** — não há implementação.
`ENABLED` é **estado-alvo**, não estado atual.

### Ciclo de promoção da capability de uma rota

```text
BLOCKED_PENDING_EVIDENCE → CANDIDATE → CERTIFIED → ENABLED
```

Promoção até `ENABLED`, com **todas** as evidências definidas atendidas,
ocorre por **configuração versionada + capability evidence aprovada**. Um
**novo ADR** só é necessário se mudar **contrato, modelo de segurança ou
arquitetura** do adapter — não para uma promoção já prevista.

## Catálogo

| Adapter | Papel arquitetural | Implementação atual | Gate atual | Estado-alvo | ADR |
| --- | --- | --- | --- | --- | --- |
| `PurviewPstImportAdapter` | `PRIMARY_GA_TARGET` | `NOT_IMPLEMENTED` | `PENDING_ADR_0006` | `ENABLED` | [ADR-0006](0006-purview-adapter-ga-inicial.md) |
| `GraphFtsTargetAdapter` | `CONDITIONAL` | `NOT_IMPLEMENTED` | `GraphFtsImportFromPstEv = BLOCKED_PENDING_EVIDENCE` | `ENABLED` após certificação | [ADR-0007](0007-graph-fts-bloqueado.md) |

Notas:

- **Purview = primeiro adapter GA planejado**, não um adapter atualmente
  habilitado em produção: não há código nem runtime e o ADR-0006 segue
  `proposto`. Quando implementado, validado em tenant e com o ADR-0006
  aceito, o estado-alvo é `ENABLED` (mantendo o capacity gate e o bloqueio
  >100 GB / `MICROSOFT_ASSESSMENT_REQUIRED`). É o primeiro destino, não o
  único.
- **Graph = segundo adapter, condicional**: disponível na arquitetura,
  selecionável só quando `GraphFtsImportFromPstEv` for promovida a
  `ENABLED`. O bloqueio é **específico à rota PST/EV → FTS**, não ao Graph
  em geral (ver ADR-0007).
- Rotas do Graph **fora** de PST/EV (ex.: round-trip de dados exportados
  pela própria API Graph) não são objeto do bloqueio deste catálogo e, se
  vierem a ser usadas, entram como capability própria.

## Manutenção

- Mudança de estado de capability (`BLOCKED_PENDING_EVIDENCE` → `CANDIDATE`
  → `CERTIFIED` → `ENABLED`) é registrada aqui, com link para a capability
  evidence e a configuração versionada que a promoveu.
- Adição de novo adapter de destino (ex.: adapter contratual de ingestão
  rápida, runbook §29) entra como nova linha, com seu ADR.
