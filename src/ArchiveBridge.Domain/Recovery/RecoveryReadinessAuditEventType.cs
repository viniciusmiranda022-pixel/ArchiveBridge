namespace ArchiveBridge.Domain.Recovery;

/// <summary>Tipo de evento da trilha auditável append-only de um <see cref="RecoveryReadinessRecord"/>.</summary>
public enum RecoveryReadinessAuditEventType : byte
{
    /// <summary>Nova versão emitida (resultado realmente diferente do vigente).</summary>
    Issued = 0,

    /// <summary>Emissão convergiu idempotentemente para a versão vigente (replay idêntico).</summary>
    Converged = 1,
}
