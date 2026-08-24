using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Referência OPACA ao material protegido dentro do secret store (ex.: chave da linha de ciphertext
/// DPAPI) — nunca o segredo em si, nunca reversível para o valor sem passar pelo adapter de custódia.
/// Permite trocar o mecanismo de secret store (DPAPI hoje; outro adapter certificado no futuro) sem
/// alterar o contrato de <see cref="PurviewSasUploadHandle"/> (work order item 6).
/// </summary>
public readonly record struct SecretStoreHandleReference
{
    private const int MaxLength = 200;

    /// <summary>Cria uma referência a partir do identificador opaco emitido pelo secret store.</summary>
    public SecretStoreHandleReference(string value) => Value = TextValue.Require(value, nameof(value), MaxLength);

    /// <summary>Identificador textual opaco.</summary>
    public string Value { get; }
}
