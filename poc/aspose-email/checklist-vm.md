# Checklist operacional da VM da PoC (preencher antes de executar)

Executor: ____________________ Data: ____________ VM: ____________________

## Isolamento e segurança

- [ ] VM Windows dedicada e **descartável** (snapshot limpo antes; wipe ao
  final), sem acesso a dados de clientes ou produção;
- [ ] Sem credenciais de produção, sem acesso a tenant real, sem Exchange;
- [ ] Rede restrita: apenas NuGet/endpoints de licença Aspose durante o
  bootstrap; sem inbound;
- [ ] Transcript do PowerShell **desabilitado** durante execuções;
- [ ] Nenhum dado real de e-mail; corpus 100% sintético (seed registrada).

## Hardware e sistema

- [ ] Windows Server suportado, com patches; relógio NTP em sincronia;
- [ ] .NET 10 SDK instalado (`dotnet --version` registrado no relatório);
- [ ] Python 3 disponível (`py -3 --version`) para o avaliador;
- [ ] Disco de dados dedicado (recomendado ≥ 1.5 TB para a escala full:
  corpus 500 GB + partes ≈ 500 GB + margem de trabalho);
- [ ] Perfil de disco/IOPS **registrado** no relatório (os números de
  throughput só têm sentido com o perfil de I/O documentado);
- [ ] Antivírus com exclusão para os diretórios de corpus/trabalho (ou
  registrado que não há exclusão — afeta métricas).

## Licença e versão

- [ ] Licença de **avaliação** Aspose.Email obtida e armazenada fora do
  repositório (caminho passado a `bootstrap.ps1 -LicensePath`);
- [ ] Versão do pacote pinada e registrada (`-AsposeVersion`);
- [ ] Confirmado que nenhum arquivo de licença ou pacote será commitado.

## Critérios antes da execução

- [ ] `criteria.json` revisado; **`operationalWindowHours500Gb` definido
  pelo responsável do gate** (valor + quem definiu + data no relatório);
- [ ] Escala `smoke` executada com sucesso antes da `full`;
- [ ] Diretórios padronizados: `D:\poc\corpus`, `D:\poc\work`,
  `D:\poc\results`, `D:\poc\metrics`.

## Pós-execução

- [ ] `evaluate.py` executado; saída anexada ao relatório;
- [ ] Relatório (`report-template.md`) preenchido e hashes conferidos;
- [ ] Logs sanitizados (sem caminhos de licença, sem segredos);
- [ ] VM destruída ou snapshot final arquivado como evidência.
