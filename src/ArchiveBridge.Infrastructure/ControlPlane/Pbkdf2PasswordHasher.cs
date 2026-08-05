using System.Security.Cryptography;
using System.Text;
using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Derivação de senha com PBKDF2-HMAC-SHA256: sal aleatório de 128 bits por usuário, subchave de 256 bits
/// e um número de iterações alto (alinhado às recomendações OWASP). A verificação recomputa a subchave com
/// as iterações ARMAZENADAS (compatível com hashes de custo diferente) e compara em TEMPO CONSTANTE. Nunca
/// manipula nem persiste a senha em claro. É a implementação da porta <see cref="IPasswordHasher"/>.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>Identificador do algoritmo/formato (versiona o esquema de derivação).</summary>
    public const string AlgorithmName = "PBKDF2-HMAC-SHA256";

    private const int DefaultIterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;

    /// <inheritdoc />
    public PortalPasswordHash Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, DefaultIterations, HashAlgorithmName.SHA256, KeySizeBytes);
        return new PortalPasswordHash(
            AlgorithmName, DefaultIterations, Convert.ToBase64String(salt), Convert.ToBase64String(derived));
    }

    /// <inheritdoc />
    public bool Verify(string password, PortalPasswordHash hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);

        // Formato desconhecido ou iterações inválidas são recusados (fail-closed), sem exceção que vaze tempo.
        if (!string.Equals(hash.Algorithm, AlgorithmName, StringComparison.Ordinal) || hash.Iterations <= 0)
        {
            return false;
        }

        if (!TryFromBase64(hash.SaltBase64, out var salt) || !TryFromBase64(hash.HashBase64, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, hash.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryFromBase64(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var buffer = new byte[((value.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written) || written == 0)
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
