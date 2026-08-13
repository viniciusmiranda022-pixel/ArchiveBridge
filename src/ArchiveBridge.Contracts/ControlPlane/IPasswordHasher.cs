namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Representação persistível de uma senha derivada (NUNCA a senha em claro): algoritmo, número de
/// iterações e os bytes de sal e da subchave em Base64. Todos os campos são necessários para reverificar
/// a senha; o formato é versionado pelo <see cref="Algorithm"/> para permitir evolução do custo sem
/// invalidar hashes antigos.
/// </summary>
public sealed record PortalPasswordHash(string Algorithm, int Iterations, string SaltBase64, string HashBase64);

/// <summary>
/// Porta de derivação e verificação de senhas do portal. A implementação usa uma KDF forte com sal por
/// usuário e comparação em tempo constante — nenhuma senha em claro é persistida, registrada em log ou
/// versionada. É uma porta para permitir teste determinístico e substituição futura por um provedor de
/// identidade externo (ex.: Windows Authentication/AD), sem tocar nas telas.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Deriva um novo hash (com sal aleatório) a partir da senha em claro.</summary>
    PortalPasswordHash Hash(string password);

    /// <summary>Verifica, em tempo constante, se a senha em claro corresponde ao hash informado.</summary>
    bool Verify(string password, PortalPasswordHash hash);
}
