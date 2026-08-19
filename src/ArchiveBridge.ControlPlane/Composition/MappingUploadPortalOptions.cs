namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Feature gate LOCAL do Portal para a superfície de UPLOAD/VALIDAÇÃO de Mapping CSV (Passo 6B). É
/// deliberadamente uma opção do próprio Control Plane — o backend seguro (<c>ArchiveBridge.Application.
/// Mapping.ValidateMappingCsvUploadUseCase</c>, Passo 6A) não conhece ASP.NET nem este gate; ele permanece
/// a autoridade final de tamanho/validação independentemente deste interruptor.
///
/// Este gate protege apenas a AÇÃO de escrita (POST de validação) — a página <c>/Mapping/Index</c>
/// continua legível pelos papéis de leitura mesmo com o gate desabilitado.
///
/// Fail-closed: o default versionado é <see langword="false"/> — sem habilitação explícita, o Portal não
/// aceita upload algum.
/// </summary>
public sealed class MappingUploadPortalOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "MappingUpload";

    /// <summary>Quando <see langword="true"/>, o Portal aceita upload/validação de Mapping CSV. Default: false.</summary>
    public bool Enabled { get; set; }
}
