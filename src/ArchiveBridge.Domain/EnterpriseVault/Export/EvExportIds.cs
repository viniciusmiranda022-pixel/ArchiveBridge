namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>Identidade opaca de um pedido de exportação EV, gerada pelo servidor (nunca pelo cliente).</summary>
public readonly record struct ExportRequestId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de pedido de exportação.</summary>
    public static ExportRequestId New() => new(Guid.NewGuid());
}

/// <summary>Identidade opaca de UMA tentativa de execução de um pedido de exportação.</summary>
public readonly record struct ExportAttemptId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de tentativa.</summary>
    public static ExportAttemptId New() => new(Guid.NewGuid());
}

/// <summary>Identidade opaca de UM output PST aceito (derivada do conteúdo — nunca do nome de arquivo).</summary>
public readonly record struct ExportOutputId(Guid Value);
