namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>Onda inexistente ou fora do escopo — anti-IDOR, indistinguível de inexistente.</summary>
public sealed class PurviewWaveNotFoundException(string message) : Exception(message);

/// <summary>
/// Archive de destino não resolvido a partir de uma onda autorizada no escopo (anti-IDOR, AB-I5-003).
/// Lançada com a MESMA mensagem genérica sempre que: a onda referenciada não existe/não pertence ao
/// tenant/projeto do chamador, o archive não faz parte da seleção da onda, ou o archive faz parte da
/// seleção mas ainda não teve a identidade resolvida por um manifesto/resolvedor autorizado. As três
/// causas produzem exatamente este mesmo tipo/mensagem — indistinguível de not-found/out-of-scope, sem
/// vazar existência, UPN, GUID ou qualquer detalhe cross-tenant/project.
/// </summary>
public sealed class PurviewArchiveNotFoundException(string message) : Exception(message);

/// <summary>
/// A URL SAS submetida foi recusada fail-closed por <c>PurviewSasIntakePolicy</c> (AB-I5-004 item 4). A
/// mensagem carrega SOMENTE o <see cref="Domain.TargetIngestion.Purview.PurviewSasRejectionReason"/>
/// estruturado — nunca qualquer fragmento da URL/segredo submetido.
/// </summary>
public sealed class PurviewSasIntakeRejectedException(
    Domain.TargetIngestion.Purview.PurviewSasRejectionReason reason)
    : Exception($"SAS recusado fail-closed: {reason}.")
{
    /// <summary>Motivo estruturado da rejeição.</summary>
    public Domain.TargetIngestion.Purview.PurviewSasRejectionReason Reason { get; } = reason;
}

/// <summary>
/// Aquisição do SAS custodiado recusada fail-closed (AB-I5-004 item 10/11) — onda/handle inexistente no
/// escopo, requester fora do boundary autorizado, ou handle fora de <see cref="Domain.TargetIngestion.Purview.SasHandleState.Available"/>
/// (Stored/Consumed/Expired/Destroyed). Todas as causas produzem o MESMO tipo/mensagem genérica —
/// indistinguível de inexistente, sem vazar qual causa específica se aplica.
/// </summary>
public sealed class PurviewSasAcquisitionDeniedException(string message) : Exception(message);
