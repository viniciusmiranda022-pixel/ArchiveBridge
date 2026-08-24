namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Identidade de workload declarada pelo chamador (ADR-0008 §"Objetivos de identidade por função" —
/// <c>ArchiveBridge-Control</c>/<c>EvWorker</c>/<c>PstWorker</c>/<c>UploadWorker</c>/<c>ReconWorker</c>).
/// Este tipo carrega apenas a AFIRMAÇÃO de identidade recebida do composition root; a vinculação dessa
/// afirmação à identidade Windows/certificado REAL do processo chamador é responsabilidade do
/// composition root/transporte autenticado de um Passo posterior (mesmo padrão já aceito por
/// <c>SubmitMailboxPrecheckRequest</c>: "Scope é resolvido pelo composition root a partir do transporte
/// autenticado" — nenhuma superfície HTTP do adapter Purview existe ainda). Dentro deste Passo, é o
/// único mecanismo de autorização testável para restringir <c>AcquireForUpload</c> ao boundary do futuro
/// <c>ArchiveBridge-UploadWorker</c> (work order AB-I5-004 item 10/11).
/// </summary>
public readonly record struct WorkloadIdentity
{
    private const int MaxLength = 100;

    /// <summary>Cria uma identidade de workload a partir de um rótulo já conhecido/documentado.</summary>
    public WorkloadIdentity(string value) => Value = TextValue.Require(value, nameof(value), MaxLength);

    /// <summary>Rótulo textual estável da identidade.</summary>
    public string Value { get; }
}

/// <summary>Identidades de workload conhecidas pela plataforma (ADR-0008).</summary>
public static class WorkloadIdentities
{
    /// <summary>Identidade dedicada do futuro upload worker on-premises — única autorizada a adquirir o SAS custodiado.</summary>
    public static WorkloadIdentity UploadWorker { get; } = new("ArchiveBridge-UploadWorker");
}
