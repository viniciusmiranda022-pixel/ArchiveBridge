using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>UM item nativo oversized bruto, reportado pelo processo exporter (AB-4C-005 item 14) — ainda não validado pelo Domain.</summary>
public sealed record EvOversizedNativeItemRaw(string ExternalItemReference, long SizeBytes, string? Diagnostic);

/// <summary>
/// Requisição TIPADA de execução de UMA tentativa de exportação — nunca uma string de comando.
/// <see cref="ConnectorVersion"/> vem da identidade do connector JÁ RESOLVIDA pela Application (nunca
/// inventada pelo executor) — é reportada de volta no <see cref="EvExportProcessResult"/> como evidência de
/// lineage (item 12), já que o engine em si (versão do cmdlet) só é conhecido depois da execução.
/// </summary>
public sealed record EvExportProcessRequest(
    EvExportCommandArguments Arguments, EvExportOutputLocation OutputLocation, string ConnectorVersion, TimeSpan Timeout, long MaxOutputBytes);

/// <summary>
/// Resultado bruto de UMA execução do processo exporter — estruturado, sem transcript bruto (item 16: o
/// transcript local, quando existe, fica protegido no host connector e nunca cruza esta fronteira).
/// </summary>
public sealed record EvExportProcessResult(
    int ExitCode,
    bool TimedOut,
    bool OutputLimitExceeded,
    string EngineVersion,
    string ConnectorVersion,
    IReadOnlyList<EvOversizedNativeItemRaw> OversizedItemsRaw,
    string? ErrorCode);

/// <summary>
/// Adapter SUBSTITUÍVEL do exporter Enterprise Vault (AB-4C-005 item 6): Domain/Application dependem SOMENTE
/// desta porta — a implementação concreta de processo/PowerShell fica em Infrastructure (Connector Host),
/// nunca atravessa Domain/Application (fronteira reforçada por <c>Architecture.Tests</c>). Nenhuma
/// implementação real deste Passo executa contra um Enterprise Vault de cliente (STOP-THE-LINE); os testes
/// usam fakes/stubs.
/// </summary>
public interface IEvArchiveExportExecutor
{
    /// <summary>Executa UMA tentativa de exportação sob os argumentos e a localização de saída já resolvidos/contidos.</summary>
    Task<EvExportProcessResult> RunAsync(EvExportProcessRequest request, CancellationToken cancellationToken);
}
