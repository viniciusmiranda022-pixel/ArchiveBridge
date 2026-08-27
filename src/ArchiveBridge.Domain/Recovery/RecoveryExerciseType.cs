namespace ArchiveBridge.Domain.Recovery;

/// <summary>
/// Tipo de exercício de recovery readiness coberto por AB-I7-005 — cada valor corresponde a uma
/// capacidade REALMENTE exercitável hoje pela arquitetura on-premises aceita (nenhum estado
/// aspiracional). Persistido como <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum RecoveryExerciseType : byte
{
    /// <summary>Backup/restore de estado canônico SQL (item 3 do work order) sobre banco de teste/efêmero.</summary>
    RestoreDrill = 0,

    /// <summary>Reconstrução determinística de trabalho pendente a partir do estado persistido (item 5).</summary>
    PendingWorkRebuild = 1,

    /// <summary>Revalidação de hash/manifesto/lineage/certificate após restore/reload (item 7).</summary>
    ArtifactEvidenceRecovery = 2,

    /// <summary>Avaliação de HA/failover de um componente da baseline atual (item 9/10) — hoje só produz <see cref="RecoveryReadinessStatus.Blocked"/>.</summary>
    HaFailover = 3,
}
