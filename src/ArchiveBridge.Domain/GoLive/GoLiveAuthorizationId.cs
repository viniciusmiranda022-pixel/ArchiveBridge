namespace ArchiveBridge.Domain.GoLive;

/// <summary>
/// Identidade OPACA de uma decisão de go-live (AB-I8-010, escopo obrigatório item 1: "criar identidade opaca,
/// tenant/project-scoped e versionada para a decisão de go-live"). Mintada UMA vez quando a primeira versão da
/// decisão de um (tenant, project) é registrada e preservada em todas as versões subsequentes da MESMA decisão
/// (drift produz uma nova <see cref="GoLiveAuthorizationDecision.AuthorizationVersion"/> da decisão existente,
/// nunca um <see cref="GoLiveAuthorizationId"/> novo) — o identificador nunca é fornecido pelo chamador.
/// </summary>
public readonly record struct GoLiveAuthorizationId(Guid Value)
{
    /// <summary>Minta uma nova identidade de decisão de go-live.</summary>
    public static GoLiveAuthorizationId New() => new(Guid.NewGuid());
}
