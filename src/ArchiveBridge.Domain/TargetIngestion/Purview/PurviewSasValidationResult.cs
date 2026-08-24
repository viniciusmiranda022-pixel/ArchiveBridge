using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Resultado de <see cref="PurviewSasIntakePolicy.Validate"/>: aceito (com metadados NÃO secretos
/// canonicalizados + o segredo redigido para a fronteira de custódia) ou rejeitado (com motivo
/// estruturado, NUNCA uma mensagem interpolando qualquer fragmento da URL/segredo — work order
/// AB-I5-004 item 5/13).
/// </summary>
public sealed class PurviewSasValidationResult
{
    private PurviewSasValidationResult(
        bool accepted,
        PurviewSasRejectionReason reason,
        string? authorizedHost,
        string? authorizedContainer,
        DateTimeOffset? expiresAtUtc,
        PurviewSasPermissions? permissions,
        Sha256Hash? fingerprint,
        RedactedSecret? secret)
    {
        Accepted = accepted;
        Reason = reason;
        AuthorizedHost = authorizedHost;
        AuthorizedContainer = authorizedContainer;
        ExpiresAtUtc = expiresAtUtc;
        Permissions = permissions;
        Fingerprint = fingerprint;
        Secret = secret;
    }

    /// <summary>Resultado aceito — todos os campos não secretos abaixo são canônicos/estruturados.</summary>
    public static PurviewSasValidationResult Accept(
        string authorizedHost,
        string authorizedContainer,
        DateTimeOffset expiresAtUtc,
        PurviewSasPermissions permissions,
        Sha256Hash fingerprint,
        RedactedSecret secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizedHost);
        ArgumentException.ThrowIfNullOrEmpty(authorizedContainer);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(secret);
        return new PurviewSasValidationResult(
            true, PurviewSasRejectionReason.None, authorizedHost, authorizedContainer, expiresAtUtc, permissions,
            fingerprint, secret);
    }

    /// <summary>Resultado rejeitado fail-closed com motivo estruturado.</summary>
    public static PurviewSasValidationResult Reject(PurviewSasRejectionReason reason)
    {
        if (reason == PurviewSasRejectionReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Uma rejeição exige um motivo diferente de None.");
        }

        return new PurviewSasValidationResult(false, reason, null, null, null, null, null, null);
    }

    /// <summary>Verdadeiro quando a URL foi aceita para custódia.</summary>
    public bool Accepted { get; }

    /// <summary>Motivo estruturado da rejeição; <see cref="PurviewSasRejectionReason.None"/> quando aceito.</summary>
    public PurviewSasRejectionReason Reason { get; }

    /// <summary>Host autorizado (metadado NÃO secreto) — presente somente quando aceito.</summary>
    public string? AuthorizedHost { get; }

    /// <summary>Container autorizado (metadado NÃO secreto, sempre <c>ingestiondata</c>) — presente somente quando aceito.</summary>
    public string? AuthorizedContainer { get; }

    /// <summary>Expiry estruturado decodificado do parâmetro <c>se</c> — presente somente quando aceito.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>Permissões estruturadas decodificadas do parâmetro <c>sp</c> — presente somente quando aceito.</summary>
    public PurviewSasPermissions? Permissions { get; }

    /// <summary>Fingerprint não reversível do segredo completo — presente somente quando aceito.</summary>
    public Sha256Hash? Fingerprint { get; }

    /// <summary>O segredo redigido, pronto para a fronteira de custódia — presente somente quando aceito.</summary>
    public RedactedSecret? Secret { get; }
}
