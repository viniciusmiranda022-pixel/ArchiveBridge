namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Identidade opaca de UMA execução de fase (Baseline/Delta/FinalDelta) de migração de archive, gerada
/// pelo servidor (nunca pelo cliente) — AB-4C-008 req 1: as três fases são estados distintos e
/// auditáveis da MESMA operação lógica, por isso compartilham um único tipo de identidade de execução.
/// </summary>
public readonly record struct EvDeltaRunId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de execução.</summary>
    public static EvDeltaRunId New() => new(Guid.NewGuid());
}

/// <summary>Identidade opaca de UMA tentativa física de execução de uma fase de delta (AB-4C-008 req 12), gerada pelo servidor.</summary>
public readonly record struct EvDeltaAttemptId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de tentativa.</summary>
    public static EvDeltaAttemptId New() => new(Guid.NewGuid());
}

/// <summary>Identidade opaca de UM watermark emitido/aceito (AB-4C-008 req 3), gerada pelo servidor.</summary>
public readonly record struct WatermarkId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de watermark.</summary>
    public static WatermarkId New() => new(Guid.NewGuid());
}

/// <summary>Identidade opaca de UM plano de freeze/cutover de um archive (AB-4C-008 req 9), gerada pelo servidor.</summary>
public readonly record struct FreezePlanId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de plano de freeze.</summary>
    public static FreezePlanId New() => new(Guid.NewGuid());
}
