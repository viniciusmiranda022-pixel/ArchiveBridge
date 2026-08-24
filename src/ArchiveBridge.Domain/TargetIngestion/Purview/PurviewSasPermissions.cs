namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Permissões estruturadas decodificadas do parâmetro <c>sp</c> de um SAS do Azure Storage (nunca uma
/// string livre repassada adiante — work order AB-I5-004 item 4). Cada flag corresponde a UMA letra
/// documentada pela Microsoft; letras não reconhecidas tornam o SAS inteiro recusado fail-closed por
/// <see cref="PurviewSasIntakePolicy"/> (nunca ignoradas silenciosamente).
/// </summary>
public sealed record PurviewSasPermissions(
    bool Read,
    bool Add,
    bool Create,
    bool Write,
    bool Delete,
    bool DeleteVersion,
    bool PermanentDelete,
    bool List,
    bool Tags,
    bool Move,
    bool Execute,
    bool Ownership,
    bool Permissions,
    bool SetImmutabilityPolicy)
{
    /// <summary>
    /// Permissão mínima ACEITÁVEL para o fluxo de upload (Create + Write — suficiente para o AzCopy
    /// depositar novos blobs no container de staging). Permissões associadas a controle administrativo do
    /// container (Delete/PermanentDelete/List/SetImmutabilityPolicy/Ownership/Permissions) são
    /// consideradas MAIS AMPLAS do que o necessário e bloqueiam fail-closed (work order item 4: "sem
    /// aceitar permissões mais amplas quando a policy vigente as proíba").
    /// </summary>
    public bool SatisfiesUploadPolicy() =>
        Create && Write
        && !Delete && !DeleteVersion && !PermanentDelete
        && !List && !SetImmutabilityPolicy && !Ownership && !Permissions;
}
