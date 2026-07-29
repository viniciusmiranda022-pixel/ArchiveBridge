using ArchiveBridge.Workers.Ev;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<EvWorker>();

var host = builder.Build();
host.Run();
