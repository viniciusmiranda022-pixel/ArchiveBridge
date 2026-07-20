# Plano de PoC — Aspose.Email como engine primária de PST (ADR-0004)

- **Status do plano:** proposto (o plano não aprova o gate; apenas o
  estrutura)
- **Gate atendido:** item 1 do gate do [ADR-0004](0004-aspose-email-engine-primaria.md)
  (PoC de biblioteca). Licença (item 2) e parecer jurídico (item 3) correm
  em paralelo e **não** são cobertos por este plano.
- **Fontes no runbook:** [§18 Seleção da engine](../runbook/03-parte-iii-conectores-e-engine-pst.md#18-seleção-da-engine-pst),
  [§19 Inspeção estrutural](../runbook/03-parte-iii-conectores-e-engine-pst.md#19-inspeção-estrutural),
  [§20 Planejamento e particionamento](../runbook/03-parte-iii-conectores-e-engine-pst.md#20-planejamento-e-particionamento),
  [§23 Validação independente](../runbook/03-parte-iii-conectores-e-engine-pst.md#23-validação-independente),
  [§45.2 Corpus PST](../runbook/06-parte-vi-plano-desenvolvimento.md#452-corpus-pst)

## 1. Objetivo

Provar, com evidência reproduzível, que a Aspose.Email for .NET sustenta as
quatro operações exigidas da engine primária — **abrir, enumerar
(inspecionar), criar e dividir** PSTs — nos limites de tamanho, formato e
anomalia do produto, antes de qualquer código de produção depender dela.

**Resultado esperado:** relatório `PASS` habilitando o item 1 do gate; ou
`FAIL` documentado, que aciona a avaliação de SDK alternativo e um novo ADR.

## 2. Pré-requisitos

- Licença **temporária/avaliação** da Aspose.Email (a licença definitiva
  OEM/deployment é o item 2 do gate, fora deste plano);
- Worker/VM Windows isolado, sem acesso a dados de clientes, espelhando o
  perfil de worker do runbook;
- .NET 10 LTS; versão exata da Aspose.Email registrada no relatório;
- Corpus sintético gerado por script versionado (dados reais só com
  autorização, mascaramento e ambiente isolado — §45.2);
- Nenhum pacote Aspose entra no repositório do produto: o código da PoC
  vive em repositório/branch descartável dedicado.

## 3. Corpus de teste (derivado da §45.2)

| Classe | Casos mínimos da PoC |
| --- | --- |
| formato | ANSI legado; Unicode atual |
| tamanho | pequeno (≤ 1 GB); fronteira 18 GiB; fronteira 20 GB; 50 GB; 100 GB; 500 GB sintético |
| conteúdo | mail, calendar, contacts, tasks, notes, distribution lists |
| estrutura | pastas profundas (≥ 20 níveis); nomes Unicode; conflito de case; pasta com 100k+ itens |
| anomalia | corrupção leve; truncado; protegido por senha |
| datas | item sem data; datas antigas/futuras; timezone/DST |
| itens | anexo grande (≥ 1 GB); recurring meetings; S/MIME; custom MAPI props |
| overlap | mesmo PST duas vezes (retry); PST diferente com conteúdo idêntico |

PSTs gigantes usam dataset sintético controlado — sparse file sozinho não
testa parser (§45.2). O gerador do corpus, os SHA-256 de cada arquivo e os
parâmetros de geração integram a evidência.

## 4. Casos de teste

### CT-1 Abertura e inspeção (§19)

Para cada PST do corpus: abrir somente leitura e coletar, sem extrair
corpos/anexos para disco: formato/versão, tamanho físico e hash, árvore de
pastas com path normalizado, contagem por pasta e total, bytes lógicos,
datas min/máx, classes MAPI, pastas hidden/non-IPM, itens não enumeráveis,
senha/criptografia, tempo, pico de memória, throughput e versão da engine.

**Passa se:** todos os PSTs sem anomalia abrem e enumeram; anomalias são
detectadas e reportadas sem crash/hang; nenhum conteúdo é gravado em disco.

### CT-2 Split por tamanho (§20.3)

`SplitInto` com alvo 18 GiB sobre os PSTs de 50/100/500 GB.

**Passa se:** todas as partes ≤ 20 GB (hard limit); reinspeção de cada
parte (CT-1) fecha contagem total; soma de itens das partes == itens
elegíveis do original; nenhuma parte corrompida na reabertura.

### CT-3 Criação e partição semântica (§20.4)

Criar PST Unicode novo, replicar subárvore e copiar mensagens por plano de
item IDs, com um único writer por vez; fechar/flush; reabrir; reinspecionar;
comparar item count e fingerprints amostrados.

**Passa se:** contagens e fingerprints batem; propriedades preservadas no
formato nativo (incl. custom MAPI props e S/MIME intactos como blobs);
reabertura pós-fechamento íntegra (§23).

### CT-4 Determinismo do plano (§20.2)

Executar o mesmo particionamento duas vezes com inputs idênticos.

**Passa se:** a associação lógica de itens é idêntica entre execuções
(mesma ordem estável por `folderPathNormalized + receivedUtc +
stableItemFingerprint`); divergência de bytes entre saídas é registrada e
avaliada conforme §20.2.

### CT-5 Robustez sob anomalia

Corrompido, truncado e protegido por senha: a engine deve falhar de forma
detectável (erro estruturado), sem consumo descontrolado de memória e sem
alterar o arquivo de origem (hash do original inalterado antes/depois).

**Passa se:** nenhum crash não tratado; original bit a bit intacto;
comportamento mapeável para os motivos de quarentena da §22.

## 5. Métricas registradas por caso

- tempo total e por fase; throughput (itens/s e MB/s), incluindo
  **throughput sustentado por janela fixa** (ex.: 5 min);
- pico de memória do processo e **série temporal de memória** ao longo da
  execução;
- **contagem de handles/file descriptors** amostrada durante execuções
  longas;
- IOPS/padrão de acesso quando disponível;
- contagens (pastas, itens por classe, não enumeráveis);
- versão exata da engine, do runtime e do SO;
- SHA-256 de entrada e de cada saída.

Metas numéricas definitivas de performance são fixadas após o primeiro
benchmark (perfis da §46 como referência). A PoC **registra** os números
para dimensionamento — mas fidelidade funcional sozinha não aprova o
item 1: os critérios mínimos de viabilidade operacional da seção 6 também
são eliminatórios. Uma biblioteca que abre e divide um PST de 500 GB e
ainda assim leva dias, vaza memória ou exige intervenção manual é
reprovada.

## 6. Critérios mínimos de viabilidade operacional (eliminatórios)

Registrados por caso e avaliados no relatório:

1. **Memória sem tendência proporcional ao tamanho do PST** — pico e
   série temporal de memória não podem crescer proporcionalmente ao
   tamanho total do arquivo (streaming, não carga integral);
2. **Sem crescimento contínuo de memória durante o split** — série
   estabiliza; crescimento monotônico ao longo do CT-2 reprova;
3. **Sem vazamento de handle/file descriptor** — contagem de handles
   estável ao longo de execuções longas e entre partes;
4. **Throughput sustentado por janela** — registrado em janelas fixas
   (ex.: 5 min); degradação progressiva sem causa externa reprova;
5. **PST de 500 GB dentro da janela operacional aprovada** — o CT-2 de
   500 GB conclui dentro da janela definida pelo responsável do gate
   antes da execução (registrada no relatório); estouro reprova;
6. **Zero crash, hang ou intervenção manual** — em todos os casos, do
   início ao fim, inclusive execuções de longa duração;
7. **Reinício/retry seguro** — interromper e reexecutar não altera o
   original (hash inalterado) e não produz duplicação lógica de itens
   nas saídas aprovadas.

## 7. Critérios de aceite do item 1 do gate

O item 1 (PoC) fecha com `PASS` somente se **todos** os critérios abaixo
forem verdadeiros:

1. CT-1 a CT-5 passam integralmente no corpus mínimo da seção 3;
2. nenhuma perda ou alteração silenciosa de item detectada (tolerâncias só
   existem após ADR e corpus de prova — §23.1);
3. limites de 18 GiB/20 GB respeitados no split;
4. anomalias falham de forma detectável e o original permanece intacto;
5. resultados reproduzíveis (CT-4);
6. **todos os critérios de viabilidade operacional da seção 6 atendidos**;
7. relatório de evidência completo (seção 8) revisado pelo responsável do
   gate registrado na [matriz de fechamento](gate-closure-matrix.md).

Qualquer critério reprovado ⇒ resultado `FAIL`, com registro do defeito,
versão testada e decisão subsequente (retestar nova versão da biblioteca ou
avaliar SDK alternativo via novo ADR).

## 8. Pacote de evidência

- relatório PoC (`PASS`/`FAIL` por caso, com logs sanitizados);
- script gerador do corpus + parâmetros + SHA-256 dos arquivos;
- métricas da seção 5 em CSV/JSON;
- versão da Aspose.Email, do .NET e do SO;
- hash do código da PoC (repositório descartável);
- data de execução e identidade de quem executou.

O pacote é anexado ao PR que fechar o gate do ADR-0004, junto com o
contrato de licença (item 2) e o parecer jurídico (item 3).

## 9. Fora de escopo

- Aprovar o gate (decisão do responsável registrado na matriz);
- Licença definitiva e parecer jurídico (itens 2 e 3);
- Qualquer código de produção ou pacote Aspose no repositório do produto;
- Dados reais de clientes.
