using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Verificação de promoção de artifact FAIL-CLOSED (AB-I7-008 item 3): compara o digest do artifact
/// candidato à promoção contra o digest da <see cref="BuildProvenanceRecord"/> aprovada — qualquer drift
/// (o candidato NÃO é bit-a-bit o que foi aprovado) é recusado por exceção, nunca silenciosamente aceito.
/// </summary>
public static class ArtifactPromotionVerifier
{
    /// <summary>Verifica que o digest do candidato corresponde exatamente ao digest da build aprovada.</summary>
    /// <exception cref="SupplyChainPromotionDriftException">O digest do candidato diverge do digest aprovado.</exception>
    public static void VerifyPromotion(BuildProvenanceRecord approvedBuild, Sha256Hash candidateArtifactDigest)
    {
        ArgumentNullException.ThrowIfNull(approvedBuild);

        if (!string.Equals(approvedBuild.ArtifactDigest.Value, candidateArtifactDigest.Value, StringComparison.Ordinal))
        {
            throw new SupplyChainPromotionDriftException(
                $"O digest do artifact candidato à promoção ({candidateArtifactDigest.Value}) diverge do digest " +
                $"aprovado para {approvedBuild.ArtifactName} versão {approvedBuild.ArtifactVersion} " +
                $"({approvedBuild.ArtifactDigest.Value}) — drift entre build aprovada e artifact promovido falha " +
                "fechado, nunca é aceito silenciosamente.");
        }
    }
}
