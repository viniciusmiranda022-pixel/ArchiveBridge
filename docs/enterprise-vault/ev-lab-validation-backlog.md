# Backlog de homologação em laboratório — Enterprise Vault

**Classificação:** Confidencial — engenharia e segurança.

Não há ambiente Enterprise Vault (EV) real disponível para laboratório no momento. A estratégia
aprovada pelo Decision Owner é **implementar por contratos**, com **documentação oficial**, **fixtures**
e **mocks controlados**, concluir os Slices, e **homologar contra EV real posteriormente** — corrigindo
adapters e sondas quando o laboratório estiver disponível.

Este documento é a fonte única das pendências que dependem de EV real. **Elas NÃO bloqueiam o PR #23**
(nem os demais Slices). Nenhum item aqui é declarado comprovado sem prova de laboratório.

## Legenda de status

- **IMPLEMENTED_WITH_FIXTURES** — o comportamento está implementado e coberto por testes determinísticos
  com fixtures/mocks controlados (sem EV real). A forma vem de documentação oficial.
- **LAB_VALIDATION_PENDING** — a confirmação contra o produto Veritas real ainda não foi feita; ao
  homologar, a sonda/adapter pode precisar de ajuste. **Nunca** declarar `LaboratoryValidated` sem essa prova.

Todos os itens abaixo estão **IMPLEMENTED_WITH_FIXTURES + LAB_VALIDATION_PENDING**, salvo indicação em contrário.

## Pendências

| # | Item | Como está implementado (fixtures/contrato) | Status |
| --- | --- | --- | --- |
| 1 | Descoberta da identidade real do ambiente | `EvEnvironmentIdentity` derivada dos metadados da sonda de `Export-EVArchive` (`observedVersion`/`productVersion`); host de fixture fornece a identidade | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 2 | Associação Directory Server / Site | Passados como argumentos tipados (`DirectoryServer`, `SiteName`) e registrados na evidência; nenhuma resolução real de topologia | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 3 | Importação de módulo / snap-in por versão | Sondas `AvailableEvModule` (`Get-Module -ListAvailable`) e `RegisteredEvSnapin` (`Get-PSSnapin -Registered`); presença como fato read-only | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 4 | Nomes e caminhos reais dos módulos | Padrão `Symantec.EnterpriseVault*` nos scripts internos; nomes/caminhos exatos por versão a confirmar | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 5 | Comportamento real dos cmdlets `Get-EV*` | Scripts internos fixos usam `Get-EVSite`/`Get-EVServer`/`Get-EVVaultStore`/`Get-EVArchive` e emitem contagens no envelope; fixtures simulam a saída | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 6 | Permissões mínimas (privilégio mínimo, nunca Domain Admin) | Capacidade `EV_REQUIRED_PERMISSIONS` derivada de `PermissionDenied` por sonda; conjunto EXATO de permissões a confirmar | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 7 | Envelopes reais de erro | Envelope JSON versionado `{schemaVersion, probe, success, errorCode, data}` com códigos `PERMISSION_DENIED`/`NOT_AVAILABLE`; classificação fail-closed por sonda | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 8 | Assinatura real de `Export-EVArchive` | Perfil documentado `EV_EXPORT_PROFILE_DOCUMENTED_15_1` (`ArchiveId`/`OutputDirectory`/`SearchString`/`Format`/`MaxThreads`/`Retry`/`MaxPSTSizeMB`); descoberta via `Get-Command` (nunca execução) | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 9 | Parameter sets | Lidos de `Get-Command … .ParameterSets` e registrados na assinatura normalizada; conjuntos reais por versão a confirmar | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 10 | `ValidateSet` do parâmetro `Format` | Não inspecionado nesta slice; o suporte a PST é julgado pela presença do parâmetro `Format`, não pelo seu `ValidateSet` | LAB_VALIDATION_PENDING |
| 11 | Confirmação do valor `PST` (do `Format`) | Presença de `Format` ⇒ `EV_EXPORT_PST_SUPPORTED`; o valor exato `PST` do `ValidateSet` a confirmar em laboratório | LAB_VALIDATION_PENDING |
| 12 | Geração real do relatório de exportação | Documentado como automático (`ExportReport_<datetime>.txt`); `EV_EXPORT_REPORT_SUPPORTED` NÃO depende de parâmetro. Geração real só no Slice de exportação, em laboratório | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 13 | Desempenho e timeout reais | Timeout obrigatório por sonda (CTS) + limite de saída em bytes; valores reais de tempo/tamanho por versão a calibrar | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 14 | Diferenças entre versões do Enterprise Vault | Seleção por CAPACIDADES (assinatura observada), não por versão textual; um único adapter documentado 15.1 hoje. Novos perfis/adapters por versão a validar | IMPLEMENTED_WITH_FIXTURES · LAB_VALIDATION_PENDING |
| 15 | Execução real de `Export-EVArchive` | **Fora de escopo desta slice** (Slice de exportação). Nenhuma sonda executa o cmdlet | LAB_VALIDATION_PENDING |
| 16 | Conexão real ao Directory Server | Read-only; nenhuma conexão real é feita nesta slice — as sondas assumem execução no host EV | LAB_VALIDATION_PENDING |

## Procedimento de homologação (quando o laboratório estiver disponível)

1. Provisionar um ambiente EV real (versão-alvo) com conta de serviço de privilégio mínimo.
2. Registrar o `IEvPowerShellHost` de produção (`WindowsEvPowerShellHost`) e habilitar a categoria de
   teste de laboratório (gated por variável de ambiente explícita — desabilitada por padrão no CI).
3. Executar a descoberta read-only e conferir a evidência publicada contra o ambiente real.
4. Fixar, por versão do EV: assinatura observada de `Export-EVArchive`, parameter sets, `ValidateSet` de
   `Format` (incluindo o valor `PST`), nomes/caminhos de módulos/snap-ins, permissões mínimas e
   envelopes de erro.
5. Corrigir os scripts internos do catálogo de sondas e/ou os adapters conforme o observado; promover
   cada item deste backlog de `LAB_VALIDATION_PENDING` para validado (e só então `LaboratoryValidated`).

## Fora de escopo (reafirmado)

Nenhuma execução real contra Enterprise Vault; nenhuma homologação de laboratório; nenhum suporte
universal a versões; nenhuma compatibilidade comprovada em produção. Sem Purview, Microsoft Graph, PST
(criação/leitura/transferência), AzCopy ou libpff.
