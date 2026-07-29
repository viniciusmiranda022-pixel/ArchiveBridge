namespace ArchiveBridge.Contracts.Jobs;

/// <summary>
/// Porta de gestão de leases: renovação (heartbeat) sob fencing e recuperação de leases expirados
/// (reaper). A recuperação é uma operação de manutenção que percorre todos os tenants sob um
/// contexto de manutenção controlado — nunca um bypass de autorização de tenant comum.
/// </summary>
public interface IJobLeaseManager
{
    /// <summary>
    /// Renova o lease do worker titular (Processing, mesmo owner e época). Um worker antigo
    /// (época defasada) é rejeitado com <see cref="JobCommandOutcome.FencedOut"/>.
    /// </summary>
    Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Recupera até <paramref name="batchSize"/> Jobs com lease expirado (Processing e
    /// <c>lease_expires_at &lt; agora</c>), devolvendo-os a RetryScheduled ou Failed conforme a
    /// política. Retorna a quantidade recuperada. Uma queda de worker não perde o Job.
    /// </summary>
    Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken);
}
