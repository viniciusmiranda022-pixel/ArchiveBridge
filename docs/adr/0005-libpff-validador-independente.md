# ADR-0005 — libpff somente como verificador independente

- **Status:** proposto
- **Data:** 2026-07-20 (versão original) · 2026-07-23 (revisão — dependência ADR-0004 e alinhamento on-premises)
- **Decision Owner:** Vinicius Miranda (aceitação formal pendente)
- **Revisor necessário:** Jurídico (licença LGPL)
- **Gate de aprovação:** análise de compatibilidade + parecer jurídico LGPL
- **Substitui / substituído por:** —

## Contexto

A [§18.1](../runbook/03-parte-iii-conectores-e-engine-pst.md#181-decisão-recomendada) posiciona libpff/pffinfo/pffexport como **validador independente**, em worker isolado, exclusivamente de leitura e verificação. A [§23 Validação independente](../runbook/03-parte-iii-conectores-e-engine-pst.md#23-validação-independente) exige que a validação combine a engine primária **e** uma engine independente (contagem por folder path, message class, fingerprints amostrados, reabertura após fechamento do writer). A [§7](../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades) define o Independent Validator como "segunda engine" que **não altera** o artefato validado.

> **Correção de dependência.** A versão original deste ADR tratava a "engine
> primária" como o [ADR-0004](0004-aspose-email-engine-primaria.md) (Aspose).
> O ADR-0004 foi **substituído pelo [ADR-0013](0013-exportacao-ev-multiversao.md)**
> (exportação Enterprise Vault multiversão): na rota EV, o **próprio EV
> extrai e segmenta os PSTs na origem** ([§16.3](../runbook/03-parte-iii-conectores-e-engine-pst.md#163-exportar-em-partes-de-18-gib),
> `Export-EVArchive -MaxPSTSizeMB`), e o Aspose saiu do caminho crítico.
> Este ADR **não depende mais do ADR-0004**: o papel da libpff — segunda
> engine de verificação, somente leitura — é **ortogonal** a qual handle
> primário produziu a part.

A baseline vigente é **on-premises** ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)); o modelo de identidade/segredos/isolamento é o do [ADR-0008](0008-isolamento-por-tenant-e-projeto.md). O "container/worker isolado" da §18.1 é realizado nesse enquadramento (ver "Isolamento on-premises").

## Decisão

Usar **libpff** (`pffinfo`/`pffexport`) exclusivamente como **segunda engine de verificação**, em **worker isolado e somente leitura**, para conferência cruzada de contagens, hierarquia de pastas e fingerprints amostrados **contra o resultado do handle primário** — seja a part produzida pela **exportação EV multiversão** ([ADR-0013](0013-exportacao-ev-multiversao.md), rota EV) ou por **ingestão de PST já existente** ([§17](../runbook/03-parte-iii-conectores-e-engine-pst.md#17-ingestão-de-pst-já-existente)). A libpff **não** é usada como writer/splitter e **não** repara artefatos. Seus tipos **nunca atravessam** `IPstEngine` (§18.2) — o domínio recebe apenas resultados normalizados. O status de licença (**LGPL**) exige **parecer jurídico antes da adoção**.

## Isolamento on-premises (ADR-0003/ADR-0008)

O "container isolado somente-leitura" da §18.1 materializa-se, na baseline on-premises, como:

- **processo isolado** em worker Windows sob **identidade de serviço dedicada e de menor privilégio** (gMSA/virtual service account — ADR-0008), distinta da identidade da engine primária;
- **acesso somente leitura** ao artefato (ACL; a part validada é imutável após validação — §33) e **diretório de trabalho `noexec`/ACL** (§34/§35);
- **sem rede** (a validação não requer saída); nenhuma execução de macro/script/preview sobre conteúdo não confiável (§35);
- invocação preferencial como **executável separado** (`pffinfo`/`pffexport`), o que também sustenta a análise LGPL (ver evidência).

Onde um cliente adotar contêinerização (ex.: Windows containers), ela é uma **realização de perfil** desse mesmo isolamento de processo — não um requisito de plataforma.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Validação com engine única (só a primária) | mais simples | sem verificação independente; cadeia de custódia mais fraca | §23 exige duas engines |
| Usar a **mesma** engine primária também como validadora | um só fornecedor/código | não é verificação independente (mesmo código, mesmos bugs) | anula o objetivo da segunda engine |
| Outra biblioteca de leitura independente | possível | requer avaliação de fidelidade e licença | libpff é a recomendada pela §18.1 |

## Consequências

- Positivas: verificação de **duas engines reais**, fortalecendo a cadeia de custódia por item, independentemente de qual handle primário produziu a part.
- Negativas / dívidas assumidas: necessário **parecer jurídico LGPL** (modelo de invocação/linkagem e distribuição) e avaliação de fidelidade; operação de mais um worker isolado.
- Riscos e mitigação: divergência silenciosa entre engines → tolerâncias só após ADR e corpus de prova ([§23.1](../runbook/03-parte-iii-conectores-e-engine-pst.md#231-tolerâncias)); **questão de licença** → parecer jurídico no gate; **contenção do uso** via processo isolado somente-leitura sob identidade de menor privilégio (ADR-0008).

## Evidências

Runbook [§18.1](../runbook/03-parte-iii-conectores-e-engine-pst.md#181-decisão-recomendada), [§23](../runbook/03-parte-iii-conectores-e-engine-pst.md#23-validação-independente), [§7](../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades). Referência oficial: repositório libpff — Apêndice F.

O gate exige **análise de compatibilidade + parecer jurídico LGPL**. A **análise técnica de compatibilidade LGPL** (modelo de invocação, linkagem, distribuição e substituibilidade) está em [`evidence/0005-analise-lgpl-libpff.md`](evidence/0005-analise-lgpl-libpff.md) (Evidence Owner: Engenharia); o **parecer jurídico é externo e permanece pendente** (revisor Jurídico a atribuir). Este ADR permanece **`proposto`** até o parecer jurídico registrado e a **aceitação formal do Decision Owner** (Vinicius Miranda).
