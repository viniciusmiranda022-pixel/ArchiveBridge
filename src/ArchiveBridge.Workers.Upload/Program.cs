using ArchiveBridge.Workers.Upload;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<UploadWorker>();

// Composição fail-closed do worker de upload Purview operacional (AB-I5-009) — independente e
// separadamente habilitável (Enabled=false por padrão).
PurviewUploadComposition.Configure(builder);

var host = builder.Build();
host.Run();
