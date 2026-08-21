using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Política OPERACIONAL de exportação, versionada (AB-4C-005 item 3): fixa o envelope EFETIVAMENTE
/// aprovado para este deployment — sempre dentro (nunca acima) do teto documentado do cmdlet
/// (<see cref="ExportRequestPolicy.MaxThreadsCeiling"/>/<see cref="ExportRequestPolicy.MaxPstSizeMbCeiling"/>).
/// Um operador pode reduzir o envelope (ex.: <c>EffectiveMaxThreads</c> menor que o teto do cmdlet, para não
/// degradar um host específico) — nunca ampliar acima do teto documentado. Também fixa timeout e limite de
/// bytes de saída capturados do processo do exporter (mesmo padrão de <c>EvDiscoveryPolicy</c>).
/// </summary>
public sealed record EvExportPolicy
{
    /// <summary>Versão corrente da política. Uma política desconhecida é recusada.</summary>
    public const int CurrentVersion = 1;

    private EvExportPolicy(int policyVersion, int effectiveMaxThreads, int effectiveMaxPstSizeMb, TimeSpan processTimeout, long maxOutputBytes)
    {
        PolicyVersion = policyVersion;
        EffectiveMaxThreads = effectiveMaxThreads;
        EffectiveMaxPstSizeMb = effectiveMaxPstSizeMb;
        ProcessTimeout = processTimeout;
        MaxOutputBytes = maxOutputBytes;
    }

    /// <summary>
    /// Cria a política operacional; fail-closed se o envelope efetivo excedesse o teto documentado do cmdlet
    /// (nunca amplia acima do que o cmdlet aceita — só pode restringir).
    /// </summary>
    public static EvExportPolicy Create(
        int policyVersion, int effectiveMaxThreads, int effectiveMaxPstSizeMb, TimeSpan processTimeout, long maxOutputBytes)
    {
        if (effectiveMaxThreads < ExportRequestPolicy.MinThreads || effectiveMaxThreads > ExportRequestPolicy.MaxThreadsCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveMaxThreads), effectiveMaxThreads,
                $"EffectiveMaxThreads deve estar entre {ExportRequestPolicy.MinThreads} e {ExportRequestPolicy.MaxThreadsCeiling}.");
        }

        if (effectiveMaxPstSizeMb < ExportRequestPolicy.MinPstSizeMb || effectiveMaxPstSizeMb > ExportRequestPolicy.MaxPstSizeMbCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveMaxPstSizeMb), effectiveMaxPstSizeMb,
                $"EffectiveMaxPstSizeMb deve estar entre {ExportRequestPolicy.MinPstSizeMb} e {ExportRequestPolicy.MaxPstSizeMbCeiling}.");
        }

        if (processTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(processTimeout), processTimeout, "O timeout do processo deve ser positivo.");
        }

        if (maxOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes), maxOutputBytes, "O limite de saída capturada deve ser positivo.");
        }

        return new EvExportPolicy(policyVersion, effectiveMaxThreads, effectiveMaxPstSizeMb, processTimeout, maxOutputBytes);
    }

    /// <summary>Política default: envelope efetivo igual ao teto documentado do cmdlet (nenhuma restrição adicional).</summary>
    public static EvExportPolicy Default { get; } = Create(
        CurrentVersion,
        ExportRequestPolicy.MaxThreadsCeiling,
        ExportRequestPolicy.MaxPstSizeMbCeiling,
        processTimeout: TimeSpan.FromHours(12),
        maxOutputBytes: 64L * 1024);

    /// <summary>Versão da política.</summary>
    public int PolicyVersion { get; }

    /// <summary>Teto EFETIVO de threads aprovado neste deployment (menor ou igual ao teto do cmdlet).</summary>
    public int EffectiveMaxThreads { get; }

    /// <summary>Teto EFETIVO de MaxPSTSizeMB aprovado neste deployment (menor ou igual ao teto do cmdlet).</summary>
    public int EffectiveMaxPstSizeMb { get; }

    /// <summary>Timeout máximo de execução do processo do exporter.</summary>
    public TimeSpan ProcessTimeout { get; }

    /// <summary>Limite de bytes capturados de stdout/stderr do processo do exporter (diagnóstico, nunca transcript bruto).</summary>
    public long MaxOutputBytes { get; }

    /// <summary>
    /// Valida a política do pedido (já validada pelo seu próprio envelope documentado) contra o teto EFETIVO
    /// deste deployment — fail-closed, sem override acima do envelope aprovado (item 3).
    /// </summary>
    public void EnsureWithinEffectiveEnvelope(ExportRequestPolicy requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.MaxThreads > EffectiveMaxThreads)
        {
            throw new EvExportValidationException(
                $"MaxThreads solicitado ({requested.MaxThreads}) excede o envelope efetivo aprovado ({EffectiveMaxThreads}).");
        }

        if (requested.MaxPstSizeMb > EffectiveMaxPstSizeMb)
        {
            throw new EvExportValidationException(
                $"MaxPstSizeMb solicitado ({requested.MaxPstSizeMb}) excede o envelope efetivo aprovado ({EffectiveMaxPstSizeMb}).");
        }
    }
}
