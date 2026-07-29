using ArchiveBridge.Workers.Upload;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<UploadWorker>();

var host = builder.Build();
host.Run();
