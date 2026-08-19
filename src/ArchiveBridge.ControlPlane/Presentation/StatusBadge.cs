namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Mapeamento semântico de um status textual para uma variante de badge (<c>success</c>/<c>warning</c>/
/// <c>danger</c>/<c>info</c>/<c>neutral</c>). Centraliza a semântica de cor: nenhuma página atribui cor
/// diretamente — todas passam pelo <see cref="Resolve"/>. Um status desconhecido cai em <c>neutral</c>
/// (fail-safe visual), nunca em uma cor enganosa.
/// </summary>
public static class StatusBadge
{
    /// <summary>Variante semântica + rótulo de exibição para um status.</summary>
    public static (string Variant, string Label) Resolve(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        var variant = value switch
        {
            // Verde — estados saudáveis/terminais positivos.
            "Ready" or "Completed" or "Valid" or "Approved" or "Active"
                or "Operational" or "Operacional" or "Conectado" or "Disponível"
                or "verified" or "accepted" or "ok" or "sucesso" => "success",

            // Locked/congelado — positivo, porém distinto: usa info.
            "Frozen" => "info",

            // Âmbar — em progresso / requer atenção.
            "Pending" or "Validating" or "ReadyForApproval" or "RetryScheduled"
                or "AssessmentRequired" or "idempotent-replay" => "warning",

            // Vermelho — falha / bloqueio.
            "Failed" or "Blocked" or "Invalid" or "Rejected" or "falha"
                or "forbidden" or "idempotency-conflict" => "danger",

            // Neutro — rascunho / substituído / indisponível / não configurado.
            "Draft" or "Superseded" or "Unsupported" or "Unavailable"
                or "Cancelled" or "Não configurado" or "Não monitorado" => "neutral",

            _ => "neutral",
        };

        return (variant, string.IsNullOrEmpty(value) ? "—" : value);
    }
}
