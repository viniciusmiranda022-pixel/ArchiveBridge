namespace ArchiveBridge.Domain.Security;

/// <summary>Desfecho REAL observado de um <see cref="IncidentResponseDrillRecord"/> — sempre a partir de uma execução real do drill, nunca alegado.</summary>
public enum IncidentResponseDrillOutcome : byte
{
    /// <summary>O mecanismo de defesa exercitado se comportou como esperado (segredo redigido, tampering detectado, cross-tenant negado).</summary>
    Contained = 0,

    /// <summary>O mecanismo de defesa exercitado NÃO se comportou como esperado — incidente real a investigar.</summary>
    Failed = 1,
}
