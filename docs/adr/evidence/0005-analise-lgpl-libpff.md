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

> [!NOTE]
> **Decisão do Decision Owner (2026-07-28): libpff FORA do MVP.** A capacidade
> `LibpffIndependentValidation` está **`BLOCKED_PENDING_EVIDENCE`** — **opcional,
> não pertencente ao MVP e não bloqueadora** do desenvolvimento. **O MVP não
> distribui** libpff / `pffinfo.exe` / `pffexport.exe` / bibliotecas LGPL
> relacionadas. Portanto, **todos os campos `BLOCKED` deste documento**
> (build, `pffinfo -V`, binário Windows, smoke) são **requisitos de habilitação
> futura**, e **não bloqueadores do MVP**. A pesquisa técnica já obtida é
> **preservada** para a certificação futura. A habilitação exige **certificação
> técnica + parecer jurídico + nova autorização do Decision Owner**.

## 0. Artefato candidato: família definida, procedimento de fixação definido; fixação concreta **parcialmente verificada**

**Família de artefato candidata e procedimento de fixação definidos; a
fixação concreta completa depende de execução pelo Evidence Owner** (itens
`BLOCKED` abaixo). O upstream `libyal/libpff` usa o layout libyal padrão
(`COPYING` = GPLv3, `COPYING.LESSER` = LGPLv3), com **SPDX efetivo
`LGPL-3.0-or-later`**; status upstream **alpha** → o build escolhido **deve
ser certificado** (§6). O pin preferencial é por **commit SHA exato**
verificado (não se presume formato de release asset).

### 0.1 Campos verificados nesta sessão (reais)

Verificados por `git ls-remote` + `git clone --branch 20231205` do upstream
(o **protocolo git smart-HTTP funciona** nesta sessão), tag candidata **`20231205`**:

| Campo | Valor verificado |
| --- | --- |
| Upstream repository | `libyal/libpff` (libyal / Joachim Metz) |
| Método de aquisição verificado | `git clone --branch 20231205 https://github.com/libyal/libpff` (git protocol) |
| **Commit SHA (pin preferencial)** | **`d8ab3594683ee9f3ec63ab0e2efd79d545854846`** (tag `20231205`, confirmado por `git ls-remote` e pelo clone) |
| License (SPDX) | **`LGPL-3.0-or-later`** (`COPYING` = GPLv3; `COPYING.LESSER` = LGPLv3) |
| SHA-256 `COPYING` | `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986` |
| SHA-256 `COPYING.LESSER` | `e3a994d82e644b03a792a930f574002658412f62407f5fee083f2555c5f23118` |
| Plataforma-alvo | **Windows** (`pffinfo.exe`/`pffexport.exe`) |

### 0.2 Campos `BLOCKED` nesta sessão (motivo registrado — não fabricados)

| Campo | Estado | Motivo |
| --- | --- | --- |
| SHA-256 do **source archive de release** | `BLOCKED` | download de release (`codeload.github.com`) retorna **HTTP 403** pelo proxy da sessão; o pin autoritativo é o **commit SHA** acima |
| `pffinfo -V` (versão) | `BLOCKED` | build falhou: `./synclibs.sh` sincroniza deps em **tags de 2026** (`libfvalue` 20260531, `libuna` 20260602, `libfwnt` 20260522) **incompatíveis** com a libpff de 2023 → `make` falha em `libfvalue`; a source distribution de release (que empacota deps compatíveis) está inacessível (403) |
| SHA-256 do **binário Linux** | `BLOCKED` | idem (sem binário construído) |
| SHA-256 do **binário Windows homologado** | `BLOCKED` | sem toolchain Windows nesta sessão |
| Smoke test (parse de PST sintético) | `BLOCKED` | depende do build acima e de um PST sintético |

> [!IMPORTANT]
> **Nada é inventado.** Os campos reais acima foram medidos nesta sessão; os
> `BLOCKED` **não** foram preenchidos com valores fictícios e **não** se
> declara smoke test executado. O Evidence Owner completa os `BLOCKED` numa das
> duas vias: (a) rodar o procedimento §0.3 num host onde o **release tarball**
> (deps compatíveis) esteja acessível; ou (b) sincronizar deps nas **tags
> compatíveis** com a libpff `20231205` antes do `make`.

### 0.3 Procedimento de fixação, build e smoke test (fail-closed)

```bash
#!/usr/bin/env bash
# Fixa e certifica o artefato candidato da libpff. Fail-closed: qualquer etapa que
# falhe aborta o script (nada de "|| true"). Rodar em host com acesso ao upstream.
set -euo pipefail
TAG="${1:?informe a tag exata verificada, ex.: 20231205}"
SAMPLE="${2:?informe o caminho de um PST sintético (sem PII)}"

# 1) Fixar por COMMIT SHA exato (preferencial; não presume formato de asset)
COMMIT="$(git ls-remote --tags --refs https://github.com/libyal/libpff \
          | awk -v t="refs/tags/${TAG}" '$2==t{print $1}')"
test -n "$COMMIT"                       # a tag/commit precisa existir
git clone https://github.com/libyal/libpff libpff-src
git -C libpff-src checkout --detach "$COMMIT"
test "$(git -C libpff-src rev-parse HEAD)" = "$COMMIT"   # pin verificado

# 2) Registrar SHA-256 dos arquivos de licença exigidos
sha256sum libpff-src/COPYING libpff-src/COPYING.LESSER

# 3) Build. Sincronizar deps em TAGS COMPATÍVEIS (ou usar o release tarball).
cd libpff-src
./synclibs.sh                            # ver nota: fixar tags de deps compatíveis
./autogen.sh
./configure
make -j"$(nproc)"
PFF="$(find . -name pffinfo -type f -perm -u+x | head -1)"; test -n "$PFF"

# 4) Smoke test (fail-closed) — versão + parse read-only sem alterar o input
"$PFF" -V                                # exit != 0 aborta (set -e)
IN="$(sha256sum "$SAMPLE" | awk '{print $1}')"
OUT_FILE="$(mktemp)"
timeout 120 "$PFF" "$SAMPLE" >"$OUT_FILE" 2>&1   # timeout; exit != 0 aborta
grep -qiE 'Personal Folder File|Number of|File header' "$OUT_FILE"  # saída estrutural esperada
test "$IN" = "$(sha256sum "$SAMPLE" | awk '{print $1}')"            # input inalterado (hash antes==depois)
sha256sum "$PFF"                         # SHA-256 do binário construído
```

O Evidence Owner registra: **commit SHA** (confirmado), SHA-256 de
`COPYING`/`COPYING.LESSER`, `pffinfo -V`, **exit code** e **stdout/stderr** do
smoke, a **presença de saída estrutural esperada**, a **ausência de crash**, a
**igualdade de hash antes/depois** do PST, o **timeout** respeitado e o
**SHA-256 do binário** (Linux e, no toolchain Windows, do `.exe` homologado).

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
