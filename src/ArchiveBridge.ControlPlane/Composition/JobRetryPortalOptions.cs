namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Feature gate LOCAL do Portal para a superfície de SOLICITAÇÃO de retry manual autorizado de Jobs
/// (Passo 7; default fail-closed = false). O backend (<c>RequestJobRetryUseCase</c> +
/// <c>IJobStore.RequestManualRetryAsync</c>) não conhece este gate — permanece a autoridade final de
/// elegibilidade/idempotência independentemente dele.
/// </summary>
public sealed class JobRetryPortalOptions
{
    public const string SectionName = "JobRetry";

    public bool Enabled { get; set; }
}
