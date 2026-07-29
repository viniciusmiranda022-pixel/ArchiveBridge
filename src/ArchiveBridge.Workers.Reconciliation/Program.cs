using ArchiveBridge.Workers.Reconciliation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ReconciliationWorker>();

var host = builder.Build();
host.Run();
