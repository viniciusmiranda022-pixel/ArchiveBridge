# Relatório da PoC — Aspose.Email (ADR-0004, item 1 do gate)

- **Resultado final:** `PASS` | `FAIL`
- **Executor:** ____________________
- **Data(s) de execução:** ____________________
- **Responsável do gate (PoC):** ____________________ (conforme
  `docs/adr/gate-closure-matrix.md`)

## 1. Ambiente

| Item | Valor |
| --- | --- |
| VM (SKU/vCPU/RAM) | |
| SO e patch level | |
| Disco de dados (tipo, IOPS/perfil, exclusão de antivírus S/N) | |
| .NET SDK (`dotnet --version`) | |
| Python (`py -3 --version`) | |
| Aspose.Email (versão pinada) | |
| Licença (tipo avaliação, sem caminho/segredo) | |
| Hash do commit deste pacote (`git rev-parse HEAD`) | |
| Ajustes de API necessários no build (diff resumido ou "nenhum") | |

## 2. Corpus

| Item | Valor |
| --- | --- |
| Seed | |
| Escalas executadas (smoke/full) | |
| Arquivos gerados + SHA-256 | anexar `corpus-manifest` |
| Janela operacional 500 GB (`operationalWindowHours500Gb`) | valor: ___ h; definido por: ___; data: ___ |

## 3. Resultados por caso

| Caso | Escopo | Status | Observações |
| --- | --- | --- | --- |
| CT-1 inspeção | todos os PSTs do corpus | | |
| CT-2 split | 50 / 100 / 500 GB | | |
| CT-3 partição semântica | | | |
| CT-4 determinismo | | | |
| CT-5 anomalias | truncado / corrompido / senha | | |

## 4. Viabilidade operacional (seção 6 do plano — eliminatória)

| Critério | Veredito do avaliador | Evidência |
| --- | --- | --- |
| Memória sem tendência proporcional ao tamanho | | |
| Sem crescimento contínuo durante split | | |
| Sem leak de handle/FD | | |
| Throughput sustentado por janela | | |
| 500 GB dentro da janela operacional | | |
| Zero crash/hang/intervenção manual | | |
| Reinício/retry sem alterar original nem duplicar | | |

Anexar a saída integral de `evaluate.py`.

## 5. Ocorrências e desvios

Registrar qualquer intervenção, reexecução, ajuste de API, comportamento
inesperado ou desvio do plano — com horário e justificativa. "Nenhum" só se
verdadeiro.

## 6. Veredito e encaminhamento

- [ ] `PASS` — habilita o item 1 do gate do ADR-0004; anexar este relatório
  e o pacote de evidência ao PR de fechamento (junto com licença — item 2 —
  e parecer jurídico — item 3).
- [ ] `FAIL` — registrar defeito, versão testada e decisão subsequente
  (retestar nova versão ou avaliar SDK alternativo via novo ADR).

Assinaturas (executor e responsável do gate), com data:
