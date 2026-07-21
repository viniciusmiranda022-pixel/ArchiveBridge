# ADR-0013 — Exportação Enterprise Vault multiversão por capability discovery

- **Status:** proposto _(evidência anexada; aguardando revisão do Decision Owner)_
- **Data:** 2026-07-20
- **Gate de aprovação:** Decision Owner + revisão Dev + Segurança
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** substitui o [ADR-0004](0004-aspose-email-engine-primaria.md)
  (Aspose como writer/splitter primário)

> **Evidência anexada (aguardando aceitação).** Revisão técnica de
> `docs/ev/` em
> [`evidence/0013-revisao-tecnica-ev.md`](evidence/0013-revisao-tecnica-ev.md)
> (Evidence Owner: Engenharia). Conclui que as especificações **documentam
> e materializam arquiteturalmente** a decisão e são tecnicamente sólidas;
> registra as lacunas L1–L6 como **condições de certificação** (por
> família, no laboratório), não de aceitação da decisão. A capability de
> **segmentação** fica **CONDICIONAL** até a obtenção read-only dos limites
> ser comprovada por família (L2). O Decision Owner **ainda revisará** antes da
> aceitação formal; o status **não** foi alterado para `aceito`.

> **Numeração:** os números **0009–0012 estão formalmente reservados** na
> tabela "ADRs subsequentes" do [`README.md`](README.md) (fingerprint por
> item, assinatura de evidência/WORM, portal, DR — espelhando a seção 9 do
> runbook). Este ADR toma o **próximo número livre após o bloco
> reservado**, 0013 — sem lacuna sem justificativa. Não substitui nenhum
> dos 0009–0012, que seguem reservados aos seus temas.

## Contexto

O runbook já usa a automação PowerShell oficial do EV no caminho de
exportação ([§16.2–16.3](../runbook/03-parte-iii-conectores-e-engine-pst.md#162-carregar-o-snap-in-e-inventariar):
`Get-EVArchive`, `Export-EVArchive -MaxPSTSizeMB`), mas assumia uma única
família de versão e delegava a segmentação/partição a uma engine PST
comercial (§18.1, §20.3 — Aspose). Ambientes reais de clientes rodam
famílias distintas do Enterprise Vault, com capacidades diferentes de
inventário, exportação, segmentação e relatório. Além disso, o Control
Plane não deve empurrar scripts arbitrários para o ambiente do cliente
(princípios de segurança da Parte V; conector outbound-only da §15).

## Decisão

> O ArchiveBridge utiliza **adapters de exportação do Enterprise Vault
> selecionados por capability discovery**. O **adapter PowerShell nativo**
> atende versões com `Export-EVArchive`. Versões anteriores utilizam
> **adapters legados explicitamente versionados, testados e
> certificados**. Ambientes **sem adapter certificado** permanecem em
> **modo assistido ou bloqueado**.

Consequências estruturais:

1. **O Enterprise Vault é o responsável por extrair e segmentar os PSTs**
   (Unicode, tamanho-alvo definido pela **política do ArchiveBridge** e
   validado contra os limites **detectados** no ambiente — ver "Política
   de tamanho de PST" abaixo). O Aspose sai do caminho crítico; o
   ADR-0004 fica **substituído** sem aprovação e a PoC correspondente sai
   do caminho crítico (PR #4 fechado sem merge; conteúdo recuperável em
   `refs/pull/4/head`).
2. **Capability discovery é obrigatório** antes de qualquer seleção de
   adapter: nenhum adapter é escolhido apenas pelo número de versão
   ([especificação](../ev/capability-discovery.md)).
3. **Contrato comum `IEvExportAdapter`** entre produto e EV
   ([contrato](../ev/adapter-contract.md)): descoberta, pré-requisitos,
   exportação, progresso, cancelamento, retry, relatório, inventário de
   PSTs e taxonomia de exceções — idênticos para todos os adapters.
4. **Framework de adapters legados**: scripts versionados por família de
   EV, assinados, com allowlist de comandos e contrato JSON de
   entrada/saída; o Control Plane **nunca envia PowerShell arbitrário**
   ([framework](../ev/legacy-adapter-framework.md)).
5. **Suporte comercial ≠ arquitetura**: a compatibilidade só é declarada
   após laboratório, testes e certificação do adapter por família de
   versão ([matriz](../ev/compatibility-matrix.md)). Ambiente sem adapter
   certificado opera em modo assistido ou é bloqueado (fail closed).
6. **Versão é candidatura, não suporte**: nenhuma família é considerada
   suportada pela string de versão. EV 12.1–15.x é a **família candidata**
   ao adapter PowerShell nativo, **sujeita à detecção das capabilities
   obrigatórias e à certificação do build** — protege contra snap-in
   ausente, cmdlet indisponível, parâmetros diferentes, permissões
   insuficientes, comportamento alterado por build e pré-requisito de
   Outlook não atendido.

### Fluxo

```text
ArchiveBridge Control Plane
        ↓
EV Capability Discovery
        ↓
Seleção do adapter
        ├── EV PowerShell Adapter        (família candidata EV 12.1–15.x;
        │                                  requer capabilities + build certificado)
        ├── EV Legacy Script Adapter     (famílias 10.x/11.x/12.0, por versão)
        └── Assisted Export Adapter      (sem adapter certificado)
        ↓
Exportação segmentada em PST Unicode
        ↓
Validação, hash e upload para o Microsoft 365
```

### Política de tamanho de PST

O tamanho de segmento **não** é presumido do EV. O capability discovery
detecta o intervalo realmente aceito no ambiente e a política do
ArchiveBridge define o alvo, validado contra esse intervalo:

| Termo | Origem | Valor |
| --- | --- | --- |
| `DetectedMinPstSizeMb` / `DetectedMaxPstSizeMb` | **detectado** pelo capability discovery no ambiente EV (build específico) | varia por ambiente |
| `ArchiveBridgeOperationalTargetMb` | **política do ArchiveBridge** (margem de segurança) | `18432` |
| `MicrosoftHardPolicyBytes` | limite duro do destino M365/Purview (referência que decidimos considerar) | `20480 MB` (20 GB) |

Regras: `18432` é o **alvo operacional do produto**, escolhido para ficar
abaixo de `MicrosoftHardPolicyBytes` com margem — **não** é um default
nativo do EV nem uma capacidade presumida. O produto **rejeita a
configuração** se `ArchiveBridgeOperationalTargetMb` não estiver dentro de
`[DetectedMinPstSizeMb, DetectedMaxPstSizeMb]`; nesse caso, ajusta-se o
alvo dentro do intervalo detectado (respeitando o limite do destino) ou o
ambiente cai para modo assistido/bloqueio.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Manter Aspose como splitter primário (ADR-0004) | independe da versão do EV | licença/jurídico no caminho crítico; reprocessa o que o EV já segmenta | EV já exporta segmentado (§16.3); dupla manipulação sem ganho |
| Selecionar adapter pelo número da versão do EV | simples | versões mentem sobre capacidades (patches, snap-ins ausentes, permissões) | capability discovery é obrigatório; vetado nos critérios de aceite |
| Scripts enviados dinamicamente pelo Control Plane | flexível | execução arbitrária no ambiente do cliente; superfície de ataque inaceitável | viola a Parte V; só scripts assinados e versionados |
| Suportar todas as versões antigas "por arquitetura" | discurso comercial | promessa sem laboratório/certificação | suporte declarado só após certificação por família |

## Consequências

- Positivas: caminho crítico sem dependência comercial de engine PST;
  segmentação na origem; superfície de segurança reduzida (allowlist +
  assinatura); suporte honesto por certificação.
- Negativas / dívidas assumidas: laboratório por família de versão do EV é
  investimento contínuo; a **ingestão de PSTs pré-existentes** (§17) perde
  o splitter primário — PSTs avulsos acima do hard limit ficam com decisão
  **adiada** (novo ADR quando esse caminho for priorizado; até lá, o
  cenário é bloqueado ou tratado no modo assistido).
- Riscos e mitigação: EV sem cmdlets esperados → discovery detecta e cai
  para legado/assistido; relatório de exportação variando por versão →
  parsing certificado por família; retry duplicando conteúdo → contrato
  exige idempotência e conjunto aprovado único.

## Entregáveis verificáveis (bloqueiam o scaffolding junto com este ADR)

| Entrega | Resultado esperado | Onde |
| --- | --- | --- |
| ADR de exportação EV multiversão | decisão arquitetural formal | este documento |
| Matriz de compatibilidade | versões, adapters e status de certificação | [`docs/ev/compatibility-matrix.md`](../ev/compatibility-matrix.md) |
| Contrato `IEvExportAdapter` | interface estável entre produto e EV | [`docs/ev/adapter-contract.md`](../ev/adapter-contract.md) |
| Capability discovery | detector de versão, cmdlets e pré-requisitos | [`docs/ev/capability-discovery.md`](../ev/capability-discovery.md) |
| Adapter EV 12.1+ | automação via PowerShell oficial | especificado no contrato; implementação pós-scaffolding |
| Framework legado | scripts específicos, assinados e versionados | [`docs/ev/legacy-adapter-framework.md`](../ev/legacy-adapter-framework.md) |
| Laboratório | ambientes representativos por família de versão | [`docs/ev/test-plan.md`](../ev/test-plan.md) |
| Testes de contrato | mesmos comportamentos para todos os adapters | [`docs/ev/test-plan.md`](../ev/test-plan.md) |
| Runbook operacional | instalação, execução, retry e troubleshooting | [`docs/ev/operational-runbook.md`](../ev/operational-runbook.md) |
| Critérios de suporte | compatível vs. testado vs. certificado | [`docs/ev/compatibility-matrix.md`](../ev/compatibility-matrix.md) |

## Evidências

Runbook [§15](../runbook/03-parte-iii-conectores-e-engine-pst.md#15-conector-de-origem-desenho-seguro),
[§16](../runbook/03-parte-iii-conectores-e-engine-pst.md#16-inventário-e-exportação-do-enterprise-vault)
(§16.3 documenta `-MaxPSTSizeMB` no intervalo 500–51200 para a versão
coberta — o discovery detecta o intervalo real por ambiente; `18432` é o
alvo operacional do ArchiveBridge, não um default do EV);
referência oficial Veritas — Apêndice F. Divergência com §18.1/§20.3
registrada em [`docs/ev/README.md`](../ev/README.md) (o DOCX v1.0
permanece fonte da conversão até revisão formal do documento).
Confirmação do gate: parecer de Arquitetura + Segurança sobre este ADR,
o contrato e a especificação de discovery.
