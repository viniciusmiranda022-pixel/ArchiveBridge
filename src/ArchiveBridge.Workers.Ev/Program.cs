using ArchiveBridge.Workers.Ev;

var builder = Host.CreateApplicationBuilder(args);

// Composição fail-closed: worker operacional real (quando habilitado) + worker sintético isolado (diagnóstico).
EnterpriseVaultDiscoveryComposition.Configure(builder);

// Composição fail-closed do worker de exportação (AB-4C-005, Slice 4C Passo 2) — independente e
// separadamente habilitável (Enabled=false por padrão).
EnterpriseVaultExportComposition.Configure(builder);

var host = builder.Build();
host.Run();
