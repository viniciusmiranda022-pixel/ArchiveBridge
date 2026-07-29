using ArchiveBridge.Workers.Pst;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<PstWorker>();

var host = builder.Build();
host.Run();
