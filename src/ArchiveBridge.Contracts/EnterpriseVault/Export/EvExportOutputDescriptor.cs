using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>
/// Identifica de forma versionada o diretório de saída de UMA tentativa de exportação (AB-4C-005 item 9).
/// <see cref="RelativeToken"/> é um caminho RELATIVO determinístico derivado EXCLUSIVAMENTE de IDs opacos do
/// servidor (tenant/projeto/connector/pedido/tentativa) — nunca de <c>ExternalArchiveId</c> ou de qualquer
/// valor fornecido pelo cliente, o que torna a superfície de path traversal estruturalmente impossível a
/// partir da entrada do usuário. A resolução para um caminho físico absoluto, sob a raiz local autorizada do
/// connector, com contenção anti-traversal/anti-reparse/anti-UNC, é responsabilidade exclusiva de
/// Infrastructure (nunca Domain/Application).
/// </summary>
public sealed record EvExportOutputDescriptor(TenantScope Scope, ConnectorId Connector, ExportRequestId Request, ExportAttemptId Attempt)
{
    /// <summary>Caminho lógico determinístico (relativo à raiz autorizada do connector).</summary>
    public string RelativeToken => string.Create(
        CultureInfo.InvariantCulture,
        $"{Scope.Tenant.Value:N}/{Scope.Project.Value:N}/{Connector.Value:N}/{Request.Value:N}/{Attempt.Value:N}");
}

/// <summary>
/// Localização RESOLVIDA (mas ainda abstrata) de um diretório de saída: a raiz local AUTORIZADA do connector
/// (configuração server-side, nunca fornecida pelo cliente) mais o token relativo determinístico. Consumida
/// exclusivamente por implementações de Infrastructure, que aplicam a contenção física real.
/// </summary>
public sealed record EvExportOutputLocation(string ConnectorAuthorizedRoot, string RelativeToken);
