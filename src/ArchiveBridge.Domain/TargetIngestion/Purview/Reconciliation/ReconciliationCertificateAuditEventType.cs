namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Tipo FECHADO de evento auditável append-only sobre um certificate (AB-I6-013 item 20: "registrar
/// audit/custody events para emissão, replay, verificação, supersession e falha de integridade, sem
/// secrets/PII indevida"). Cada valor mapeia 1:1 para os cinco eventos exigidos pelo work order.
/// </summary>
public enum ReconciliationCertificateAuditEventType : byte
{
    /// <summary>Uma nova versão de certificate foi persistida (candidato não convergiu para uma versão existente).</summary>
    Issued = 0,

    /// <summary>Um pedido de emissão convergiu idempotentemente para uma versão já persistida (replay idêntico).</summary>
    Converged = 1,

    /// <summary>Um certificate existente foi lido/reidratado e sua integridade foi revalidada com sucesso.</summary>
    Verified = 2,

    /// <summary>A revalidação de integridade de um certificate persistido falhou (hash recomputado divergente).</summary>
    IntegrityViolationDetected = 3,

    /// <summary>
    /// Um certificate previamente vigente foi identificado como superseded/stale porque a evidência canônica
    /// (avaliação e/ou dispositions) avançou desde a sua emissão.
    /// </summary>
    Superseded = 4,
}
