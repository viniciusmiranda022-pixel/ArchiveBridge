# Pacote de execução da PoC — Aspose.Email (ADR-0004, item 1 do gate)

Este diretório contém o pacote **executável e descartável** da PoC definida
em [`docs/adr/0004-poc-plan.md`](../../docs/adr/0004-poc-plan.md). Ele
existe para produzir o **relatório PASS/FAIL** que fecha (ou reprova) o
item 1 do gate do [ADR-0004](../../docs/adr/0004-aspose-email-engine-primaria.md).

> [!IMPORTANT]
> **Nenhum pacote Aspose entra neste repositório.** Este diretório versiona
> apenas fontes, scripts e critérios. A referência ao `Aspose.Email` é
> criada **somente na VM descartável**, por `scripts/bootstrap.ps1`, após a
> licença de avaliação existir. Nada aqui é código de produção; nenhum
> projeto/solution do produto referencia este diretório. Após o fechamento
> do gate, este diretório pode ser removido ou arquivado como evidência.

> [!NOTE]
> As fontes C# foram escritas **sem compilação neste repositório** (por
> design, não há Aspose aqui). O primeiro passo na VM é `bootstrap.ps1` +
> `dotnet build`; pequenos ajustes de superfície de API são esperados e
> fazem parte da execução da PoC — registre-os no relatório.

## Conteúdo

| Item | Arquivo |
| --- | --- |
| Checklist operacional da VM | [`checklist-vm.md`](checklist-vm.md) |
| Critérios automáticos de PASS/FAIL | [`criteria.json`](criteria.json) |
| Modelo do relatório final | [`report-template.md`](report-template.md) |
| Bootstrap do projeto descartável (na VM) | [`scripts/bootstrap.ps1`](scripts/bootstrap.ps1) |
| Coletor externo de métricas | [`scripts/collect-metrics.ps1`](scripts/collect-metrics.ps1) |
| Avaliador automático (stdlib Python) | [`scripts/evaluate.py`](scripts/evaluate.py) |
| Fontes C# (gerador de corpus + CT-1…CT-5) | [`src/`](src/) |

## Fluxo de execução (na VM descartável)

1. Preparar a VM conforme [`checklist-vm.md`](checklist-vm.md) — inclusive
   **definir a janela operacional do teste de 500 GB** em `criteria.json`
   (`operationalWindowHours500Gb`), decisão do responsável do gate **antes**
   da execução.
2. Clonar este repositório na VM e rodar:
   ```powershell
   cd poc\aspose-email
   .\scripts\bootstrap.ps1 -AsposeVersion <versao-pinada> -LicensePath C:\poc\Aspose.Email.lic
   ```
3. Gerar o corpus (§45.2): primeiro `-Scale smoke` para validar o harness,
   depois `-Scale full`:
   ```powershell
   .\disposable\AsposePoc\bin\Release\net10.0\AsposePoc.exe corpus --out D:\poc\corpus --scale smoke --seed 42
   ```
4. Executar cada caso com o coletor externo em paralelo:
   ```powershell
   $p = Start-Process ...AsposePoc.exe -ArgumentList 'ct2','--pst','D:\poc\corpus\c500gb.pst','--workdir','D:\poc\work','--results','D:\poc\results' -PassThru
   .\scripts\collect-metrics.ps1 -ProcessId $p.Id -OutCsv D:\poc\metrics\ct2-500gb.csv
   ```
5. Avaliar automaticamente:
   ```powershell
   py -3 .\scripts\evaluate.py --results D:\poc\results --metrics D:\poc\metrics --criteria .\criteria.json
   ```
   Saída: veredito por critério + veredito final; exit code `0` = PASS,
   `1` = FAIL.
6. Preencher [`report-template.md`](report-template.md) com os resultados e
   anexar o pacote de evidência (seção 8 do plano) ao PR de fechamento do
   gate.

## O que o harness cobre

- `corpus` — gera PSTs sintéticos determinísticos (seed) por classe da
  §45.2, cada um com `*.expected.json` (manifesto de itens/pastas esperados,
  gerado a partir do plano de conteúdo, **não** da leitura do PST) e
  `*.sha256`. Anomalias (truncado/corrompido) são derivadas por manipulação
  binária de cópias, fora da engine.
- `ct1` — abertura/inspeção sem extração (§19): formato, contagens por
  pasta, classes, datas, itens não enumeráveis, comparação com o manifesto.
- `ct2` — split por tamanho (§20.3) com verificação de hard limit e
  reinspeção de cada parte.
- `ct3` — partição semântica mínima (§20.4): novo PST Unicode, réplica de
  subárvore, cópia por plano de IDs, reabertura e comparação.
- `ct4` — determinismo (§20.2): dois planejamentos com inputs idênticos e
  comparação da associação lógica.
- `ct5` — anomalias (§22): truncado, corrompido, senha; original intacto.
- Métricas internas (memória/handles/progresso) emitidas pelo próprio
  processo + coletor externo independente; o avaliador cruza os dois.

## Interpretação metodológica

O corpus é gerado **com a própria engine sob teste** (não há writer PST
alternativo — exatamente a razão do ADR-0004). A independência da
verificação vem de: (a) manifestos de conteúdo esperado gerados a partir do
**plano de geração**, não da leitura; (b) hashes e manipulações binárias
externas à engine; (c) contagem cruzada com libpff quando disponível na VM
(opcional nesta PoC; obrigatória no produto — ADR-0005).
