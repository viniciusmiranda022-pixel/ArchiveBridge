# Exportação Enterprise Vault multiversão — conjunto de especificações

Documenta a revisão arquitetural aprovada para proposta no
[ADR-0013](../adr/0013-exportacao-ev-multiversao.md): **o Enterprise Vault
extrai e segmenta os PSTs na origem**, por meio de adapters selecionados
por **capability discovery**. Nenhum adapter é escolhido pelo número de
versão; nenhum script arbitrário é executado a mando do Control Plane;
ambiente sem adapter certificado opera em modo assistido ou bloqueado.

```text
ArchiveBridge Control Plane
        ↓
EV Capability Discovery
        ↓
Seleção do adapter
        ├── EV PowerShell Adapter        (EV 12.1–15.x, Export-EVArchive)
        ├── EV Legacy Script Adapter     (famílias 10.x/11.x/12.0)
        └── Assisted Export Adapter      (sem adapter certificado)
        ↓
Exportação segmentada em PST Unicode
        ↓
Validação, hash e upload para o Microsoft 365
```

## Documentos

| Documento | Conteúdo |
| --- | --- |
| [`capability-discovery.md`](capability-discovery.md) | detector de versão/build, cmdlets, capacidades e pré-requisitos |
| [`adapter-contract.md`](adapter-contract.md) | contrato comum `IEvExportAdapter` + taxonomia de exceções |
| [`compatibility-matrix.md`](compatibility-matrix.md) | matriz por versão/família, níveis de automação e **critérios de suporte** (compatível vs. testado vs. certificado) |
| [`legacy-adapter-framework.md`](legacy-adapter-framework.md) | scripts versionados por família, assinatura, allowlist e contrato JSON |
| [`test-plan.md`](test-plan.md) | laboratório por família, plano de testes, testes de contrato e **critérios de aceite** |
| [`operational-runbook.md`](operational-runbook.md) | instalação, execução, retry e troubleshooting do conector |

## Entregáveis verificáveis

Nenhuma tarefa genérica ("suportar versões antigas do EV"): o planejamento
se fecha nas entregas abaixo, cada uma com resultado verificável.

| Entrega | Resultado esperado | Estado |
| --- | --- | --- |
| ADR de exportação EV multiversão | decisão arquitetural formal | [proposto](../adr/0013-exportacao-ev-multiversao.md) |
| Matriz de compatibilidade | versões, adapters e status de certificação | [redigida](compatibility-matrix.md) |
| Contrato `IEvExportAdapter` | interface estável entre produto e EV | [especificado](adapter-contract.md) |
| Capability discovery | detector de versão, cmdlets e pré-requisitos | [especificado](capability-discovery.md) |
| Adapter EV 12.1+ | automação via PowerShell oficial | especificado; implementação pós-scaffolding |
| Framework legado | scripts específicos, assinados e versionados | [especificado](legacy-adapter-framework.md) |
| Laboratório | ambientes representativos por família de versão | [planejado](test-plan.md) |
| Testes de contrato | mesmos comportamentos para todos os adapters | [planejados](test-plan.md) |
| Runbook operacional | instalação, execução, retry e troubleshooting | [redigido](operational-runbook.md) |
| Critérios de suporte | compatível vs. testado vs. certificado | [definidos](compatibility-matrix.md) |

## Divergência registrada com o runbook v1.0

O DOCX (`docs/source/`) permanece a fonte da conversão de `docs/runbook/`
e **não foi alterado**; conforme a regra do próprio runbook (bloquear,
registrar ADR e atualizar matriz de capacidades quando houver
divergência), esta revisão registra:

| Runbook v1.0 | Revisão (ADR-0013) |
| --- | --- |
| §18.1: Aspose.Email como engine primária (abrir, enumerar, criar, dividir) | Aspose fora do caminho crítico; EV segmenta na origem |
| §20.3: split simples por tamanho com Aspose | segmentação por `Export-EVArchive -MaxPSTSizeMB` (§16.3, mantida) |
| §16: assume host EV único com snap-in disponível | capability discovery multiversão + famílias legadas + modo assistido |
| §17: ingestão de PST pré-existente com engine para split | decisão **adiada** para novo ADR; cenário acima do hard limit bloqueado ou assistido |

A incorporação desta revisão ao DOCX (v1.1) é ação pendente do owner do
documento; após a nova versão, reconverter (`tools/convert_runbook.py`) e
atualizar o `conversion-report.md`.
