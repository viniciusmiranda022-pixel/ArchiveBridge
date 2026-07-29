namespace ArchiveBridge.Domain.Operations;

/// <summary>Disposição de retry (runbook §9.2). Ambíguo/permanente não repete automaticamente.</summary>
public enum RetryDisposition
{
    RetryTransient,
    DoNotRetry,
    OperatorActionRequired,
}
