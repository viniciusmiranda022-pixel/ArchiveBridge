namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Tipo de exercício de incident-response sintético e não destrutivo (AB-I7-008 item 5) — cada valor
/// corresponde a um dos três drills mínimos exigidos pelo work order. Persistido como <c>TINYINT</c> com
/// o MESMO valor numérico desta enum.
/// </summary>
public enum IncidentResponseDrillType : byte
{
    /// <summary>Um segredo canário sintético é injetado em texto de evidência e verificado como redigido antes de persistir.</summary>
    SecretLeakCanary = 0,

    /// <summary>Um registro de evidência persistido é adulterado fora do caminho de escrita e a revalidação de hash é verificada como fail-closed.</summary>
    HashMismatchTampering = 1,

    /// <summary>Uma tentativa de ler/escrever a evidência de outro tenant é verificada como negada pela RLS.</summary>
    CrossTenantDenial = 2,
}
