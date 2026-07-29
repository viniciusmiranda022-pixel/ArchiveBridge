// SCAFFOLDING NÃO FUNCIONAL — plano de controle (ADR-0001).
// Expõe apenas liveness. NÃO há API de migração, portal, autenticação, acesso a segredos,
// banco de dados nem qualquer integração externa. A superfície real entra por slice posterior.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness apenas: prova que o host sobe. Não indica prontidão de nenhuma capacidade de migração.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();
