using ArchiveBridge.Workers.Pst;
using ArchiveBridge.Workers.Pst.Composition;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<PstWorker>();

// Slice 4B, Passo 1: registra os serviços de inspeção PST quando habilitados (fail-closed; ver
// PstInspectionOptions/PstInspectionComposition). Não registra worker/fila — só o caso de uso
// diretamente invocável, base para orquestração assíncrona de um Passo futuro.
PstInspectionComposition.Configure(builder);

var host = builder.Build();
host.Run();
