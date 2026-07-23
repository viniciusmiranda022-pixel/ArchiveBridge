# Análise técnica de compatibilidade LGPL da libpff — gate do ADR-0005

Evidência requerida pelo gate do
[ADR-0005](../0005-libpff-validador-independente.md) (libpff somente como
verificador independente).

- **Tipo:** análise **técnica** de compatibilidade de licença (insumo do parecer jurídico)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge
- **Revisor necessário:** Jurídico
- **Estado:** **pendente de parecer jurídico** — este documento organiza os
  fatos técnicos do uso e as perguntas objetivas para o Jurídico. **Não é
  parecer jurídico, não é aconselhamento legal e não é a aceitação formal.**

> [!IMPORTANT]
> A licença exata da libpff (família **LGPL**) e seus termos são **insumo do
> parecer**: a análise abaixo **não afirma** conformidade — descreve o modelo
> de uso que a sustenta e delimita o que o Jurídico deve decidir.

## 1. Fatos técnicos do uso (o que o produto faz)

1. A libpff é utilizada **apenas como validador independente**, somente
   leitura — **nunca** como writer/splitter e **nunca** para reparo
   ([ADR-0005](../0005-libpff-validador-independente.md); §18.1, §23).
2. Invocação **preferencial como executável separado** (`pffinfo` /
   `pffexport`), em **processo isolado** sob identidade de menor privilégio
   (gMSA — [ADR-0008](../0008-isolamento-por-tenant-e-projeto.md)).
3. Os **tipos da libpff nunca atravessam** `IPstEngine` (§18.2): o domínio
   recebe apenas resultados normalizados. Não há acoplamento de API do
   produto aos tipos da biblioteca.
4. O produto **não modifica** o código-fonte da libpff (uso da ferramenta
   como publicada).
5. O produto é **instalado on-premises** na infra do cliente (ADR-0003) — a
   questão de "distribuição" depende de **como** o binário da libpff chega ao
   host (ver seção 3).

## 2. Modelos de vínculo e implicação (a decidir pelo Jurídico)

| Modelo | Descrição | Postura LGPL (a confirmar juridicamente) |
| --- | --- | --- |
| **A — Executável separado (preferido)** | o produto **executa** `pffinfo`/`pffexport` como processo separado, trocando dados por arquivos/stdout | menor acoplamento; tende a ser o modelo mais seguro (uso do programa, não vínculo de biblioteca) — **preferir e padronizar** |
| **B — Biblioteca dinâmica** | se algum componente vincular `libpff` dinamicamente | "combined work" da LGPL: exige, tipicamente, **linkagem dinâmica + capacidade de relink/substituição**, aviso de licença e não restringir engenharia reversa para depuração de modificações do usuário |
| **C — Linkagem estática** | vincular estaticamente a libpff ao binário do produto | **evitar**: aumenta as obrigações da LGPL e é desnecessário dada a arquitetura de processo isolado |

**Decisão de engenharia:** padronizar o **modelo A**. O modelo C é
explicitamente evitado; o modelo B, se algum dia necessário, entra com
análise jurídica própria.

## 3. Distribuição (pergunta central para o Jurídico)

- **Se o produto redistribui** o binário/ferramentas da libpff junto do
  instalador on-premises: tipicamente é preciso **incluir o texto da
  licença**, aviso de uso, e uma **oferta de código-fonte correspondente**
  (upstream não modificado); se houvesse modificação, disponibilizar a fonte
  modificada.
- **Se o cliente instala a libpff separadamente** (o produto apenas a invoca
  quando presente): a postura de distribuição muda. **Qual dos dois** é
  adotado é decisão de produto + Jurídico e deve ser registrada.

## 4. Substituibilidade (reforço arquitetural)

Como os tipos da libpff **não atravessam** `IPstEngine`, o validador
independente é **substituível** por outra engine independente sem alterar o
domínio. Isso **satisfaz naturalmente** o requisito da LGPL de o usuário
poder **substituir/relinkar** a versão da biblioteca — a fronteira já é um
ponto de troca.

## 5. Perguntas objetivas para o parecer jurídico

1. Qual a **versão exata da LGPL** aplicável à libpff e às ferramentas
   `pffinfo`/`pffexport`, e quais obrigações ela impõe no **modelo A**?
2. O **modelo A (executável separado)** qualifica-se como uso do programa
   (não gerando obrigação de abertura do produto) na jurisdição aplicável?
3. Adotar **redistribuição** com o instalador **ou** exigir instalação
   separada pelo cliente? Qual mecanismo de **oferta de fonte** correspondente?
4. Há obrigações adicionais (patentes, marcas, exportação) relevantes ao
   contexto do cliente?
5. Requisitos de **atribuição/aviso** a incluir na documentação e no
   instalador.

## 6. Riscos residuais

- Uso inadvertido da libpff fora do papel de validador (ex.: como writer) —
  **mitigação:** `IPstEngine` só recebe resultados normalizados; revisão de
  arquitetura no CI (§37).
- Linkagem estática acidental (modelo C) — **mitigação:** padronizar modelo A
  e proibir C sem novo ADR/parecer.
- Divergência entre a versão da libpff testada e a distribuída —
  **mitigação:** fixar versão + hash, como nos demais binários homologados.

## 7. Conclusão e assinatura (a preencher na revisão)

- **Parecer jurídico (LGPL) — assinatura/data:** _(pendente)_
- **Modelo de distribuição decidido (A/redistribuição × instalação separada):** _(pendente)_
- **Ressalvas/condições:** _(pendente)_

A **aceitação formal** do ADR-0005 é ato do Decision Owner (Vinicius
Miranda) e ocorre **somente após** o parecer jurídico estar registrado —
conforme a [matriz de fechamento](../gate-closure-matrix.md).
