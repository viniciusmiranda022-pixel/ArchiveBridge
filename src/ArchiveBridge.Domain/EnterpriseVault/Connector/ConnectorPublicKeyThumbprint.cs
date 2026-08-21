namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>
/// Thumbprint SHA-256 (hex minúsculo, 64 caracteres) da chave pública do connector — material PÚBLICO
/// vinculado à identidade; a chave privada nunca é conhecida ou persistida pelo Control Plane (§15.1,
/// AB-4C-001 critério 2). Formato validado na FORMA, sem depender de nenhuma biblioteca de certificado.
/// </summary>
public readonly record struct ConnectorPublicKeyThumbprint
{
    /// <summary>Valida o formato (64 caracteres hexadecimais minúsculos) — fail-closed.</summary>
    public ConnectorPublicKeyThumbprint(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64 || !IsLowerHex(value))
        {
            throw new ArgumentException(
                "Thumbprint deve ter exatamente 64 caracteres hexadecimais minúsculos (SHA-256).", nameof(value));
        }

        Value = value;
    }

    /// <summary>Representação hexadecimal minúscula do thumbprint.</summary>
    public string Value { get; }

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
        {
            var isLowerHexDigit = (character is >= '0' and <= '9') || (character is >= 'a' and <= 'f');
            if (!isLowerHexDigit)
            {
                return false;
            }
        }

        return true;
    }
}
