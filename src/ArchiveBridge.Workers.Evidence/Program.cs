using ArchiveBridge.Workers.Evidence;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<EvidenceWorker>();

var host = builder.Build();
host.Run();
