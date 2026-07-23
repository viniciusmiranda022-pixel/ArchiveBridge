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
> A licença e os termos exatos são **insumo do parecer**: a análise abaixo
> **não afirma** conformidade e **não contém conclusões jurídicas** — descreve
> o modelo de uso e delimita o que o Jurídico deve decidir sobre um
> **artefato específico**.

## 0. Artefato analisado (fixar antes do parecer)

O parecer jurídico deve analisar **um artefato específico**, não o projeto de
forma abstrata. O repositório oficial declara **LGPL-3.0-or-later** e status
**alpha** — o que **exige certificar o build escolhido**. Registrar:

| Campo | Valor |
| --- | --- |
| Upstream repository | `libyal/libpff` |
| License | **LGPL-3.0-or-later** |
| Pinned commit/tag | _(a fixar)_ |
| Binary version | _(a fixar)_ |
| SHA-256 | _(a fixar)_ |
| Included license files | `COPYING`, `COPYING.LESSER` |
| Upstream status | **alpha** → build escolhido deve ser certificado antes do uso |

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

Descrição técnica dos modelos (o **efeito jurídico de cada um é do parecer**, não da engenharia):

| Modelo | Descrição técnica | Decisão de engenharia |
| --- | --- | --- |
| **A — Executável separado** | o produto **executa** `pffinfo` (padrão) como processo separado, trocando dados por arquivos/stdout | **padronizar** — é o modelo de menor acoplamento técnico |
| **B — Biblioteca dinâmica** | algum componente vincula `libpff` dinamicamente | **não adotar** sem análise jurídica própria |
| **C — Linkagem estática** | vincular estaticamente a libpff ao binário do produto | **proibir** sem novo ADR/parecer |

As obrigações associadas a cada modelo — **combined work, oferta de fonte,
relink, engenharia reversa, atribuição** — são **perguntas ao Jurídico**
(seção 5), **não** conclusões deste documento.

## 3. Distribuição (pergunta central para o Jurídico)

Dois cenários possíveis, cuja implicação jurídica é do parecer:

- **O produto redistribui** o binário/ferramentas da libpff junto do
  instalador on-premises; **ou**
- **o cliente instala a libpff separadamente** e o produto apenas a invoca
  quando presente.

**Qual cenário adotar**, e quais obrigações dele decorrem (texto de licença,
aviso, oferta de fonte correspondente, tratamento de eventual modificação),
são **decisão de produto + Jurídico** e devem ser registradas — este
documento **não** as pré-decide.

## 4. Substituibilidade (fato arquitetural, sem conclusão jurídica)

Como os tipos da libpff **não atravessam** `IPstEngine`, o validador
independente é **substituível** por outra engine independente sem alterar o
domínio. A separação por processo e a ausência de tipos libpff no domínio
**reduzem o acoplamento técnico**. **O efeito jurídico desse modelo
(inclusive quanto a relink/substituição na LGPL) será determinado
exclusivamente pelo parecer jurídico sobre LGPL-3.0-or-later** — a engenharia
não conclui que o desenho "satisfaz" qualquer obrigação legal.

## 5. Contrato do processo libpff (verificável)

O ADR exige um **contrato versionado** request/result para o processo de validação, de modo que a saída seja auditável e o input jamais alterado:

```text
LibpffValidationRequest
  - artifact_id
  - canonical_input_path
  - expected_sha256
  - validation_profile
  - timeout
  - resource_limits

LibpffValidationResult
  - tool_version
  - tool_sha256
  - exit_code
  - parse_status
  - folder_count
  - item_count
  - normalized_folder_summary
  - sampled_fingerprints
  - warnings
  - error_code
```

## 6. Plano de compatibilidade

O build escolhido (status upstream **alpha**) deve ser certificado contra:

- encoding; locale; stdout; stderr; exit codes; timeout;
- arquivos corrompidos; PST **ANSI**; PST **Unicode**; PST grande;
- memória; CPU; cancellation; **ausência de rede**;
- **nenhuma alteração do input** — hash **antes e depois** idênticos.

## 7. Perguntas objetivas para o parecer jurídico

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

## 8. Riscos residuais

- Uso inadvertido da libpff fora do papel de validador (ex.: como writer) —
  **mitigação:** `IPstEngine` só recebe resultados normalizados; revisão de
  arquitetura no CI (§37).
- Linkagem estática acidental (modelo C) — **mitigação:** padronizar modelo A
  e proibir C sem novo ADR/parecer.
- Divergência entre a versão da libpff testada e a distribuída —
  **mitigação:** fixar versão + hash, como nos demais binários homologados.

## 9. Conclusão e assinatura (a preencher na revisão)

- **Parecer jurídico (LGPL-3.0-or-later) — assinatura/data:** _(pendente)_
- **Artefato fixado (commit/tag + versão + SHA-256) analisado no parecer:** _(pendente)_
- **Modelo de distribuição decidido (redistribuição × instalação separada):** _(pendente)_
- **Ressalvas/condições:** _(pendente)_

Para este gate, a **exceção de bootstrap** (competência de revisão exercida
pelo Decision Owner) **não se aplica**: exige-se **parecer jurídico externo
real**; a evidência **não** simula esse parecer. A **aceitação formal** do
ADR-0005 é ato do Decision Owner (Vinicius Miranda) e ocorre **somente após**
o parecer jurídico estar registrado — conforme a
[matriz de fechamento](../gate-closure-matrix.md).
