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
