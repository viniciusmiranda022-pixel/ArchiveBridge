# Framework de adapters legados do Enterprise Vault

Para famílias sem `Export-EVArchive` confiável (10.x, 11.x, 12.0), o
produto usa **adapters legados**: scripts específicos por família,
empacotados, assinados e certificados — nunca PowerShell arbitrário
([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)).

## Princípios de segurança

1. **O Control Plane nunca envia script para execução.** O conector
   executa apenas scripts que **já estão no pacote assinado instalado**;
   o Control Plane envia somente *comandos do contrato* (JSON) com IDs e
   parâmetros tipados.
2. **Assinatura obrigatória**: cada pacote de adapter legado é assinado
   (Authenticode); o conector recusa pacote sem assinatura válida ou com
   hash divergente do manifesto.
3. **Allowlist de comandos**: o runner do conector só permite os
   executáveis/cmdlets listados no manifesto do adapter; qualquer chamada
   fora da allowlist aborta a execução com `EV_EXPORT_PERMANENT`.
4. **Sem interpolação de strings em shell**: parâmetros entram por
   argumentos tipados/arquivo de entrada, nunca concatenados em linha de
   comando.

## Estrutura de um adapter legado

```text
ev-legacy-<familia>@<versao>/
  manifest.json          ← família EV alvo, versão do adapter, allowlist,
                            hashes dos scripts, assinatura
  scripts/
    discover.ps1         ← inventário de archives da família
    precheck.ps1         ← validação de pré-requisitos
    export.ps1           ← exportação segmentada (ou orquestração da
                            ferramenta nativa da família)
    report.ps1           ← normalização do relatório da família
  contract/
    input.schema.json    ← contrato JSON de entrada (por operação)
    output.schema.json   ← contrato JSON de saída (por operação)
```

## Contrato JSON de entrada/saída

- Cada operação do [`IEvExportAdapter`](adapter-contract.md) mapeia para
  uma invocação `script + input.json → output.json`.
- Entrada e saída são validadas contra os schemas do pacote **antes** de
  o resultado ser aceito; saída fora do schema = `EV_OUTPUT_INCONSISTENT`.
- A saída nunca contém assunto, corpo, credencial ou SAS — apenas IDs,
  contagens, caminhos de staging e códigos da taxonomia comum.

## Versionamento e certificação

- Identidade do adapter: `ev-legacy-<familia>@<versao>` (ex.:
  `ev-legacy-11x@2`); qualquer mudança de script gera nova versão.
- Cada versão passa pelos **testes de compatibilidade da família**
  ([test-plan.md](test-plan.md)) antes de promoção na
  [matriz](compatibility-matrix.md) (`planejado → em laboratório →
  certificado`).
- O relatório de certificação registra: família e builds testados,
  resultados por caso, limitações conhecidas e hash do pacote assinado.
- Revogação: vulnerabilidade ou regressão revoga a versão (lista de
  revogação distribuída ao conector); versão revogada não carrega.

## Modo assistido (Assisted Export Adapter)

Quando não há adapter certificado, o mesmo contrato é servido por um
adapter **assistido**: o produto gera instruções passo a passo para o
operador do cliente executar a exportação nativa da versão, e então
valida, calcula hash, inventaria e ingere os PSTs produzidos. A
automação é zero, mas custódia, validação e evidência permanecem
idênticas às dos adapters automatizados.
