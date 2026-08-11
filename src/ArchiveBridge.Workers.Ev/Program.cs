using ArchiveBridge.Workers.Ev;

var builder = Host.CreateApplicationBuilder(args);

// Composição fail-closed: worker operacional real (quando habilitado) + worker sintético isolado (diagnóstico).
EnterpriseVaultDiscoveryComposition.Configure(builder);

var host = builder.Build();
host.Run();
