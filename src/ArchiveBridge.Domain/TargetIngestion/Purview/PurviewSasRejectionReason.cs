namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Motivo estruturado de rejeição fail-closed da URL SAS (work order AB-I5-004 item 4) — NUNCA uma
/// mensagem livre interpolando qualquer fragmento da URL/segredo (mesmo padrão de
/// <see cref="PurviewPrecheckBlockReason"/>). <see cref="None"/> é o default fail-closed do enum.
/// </summary>
public enum PurviewSasRejectionReason
{
    /// <summary>Sem rejeição — a URL pode prosseguir para custódia.</summary>
    None,

    /// <summary>A string não é uma URI absoluta bem formada.</summary>
    MalformedUri,

    /// <summary>Esquema diferente de <c>https</c>.</summary>
    SchemeNotHttps,

    /// <summary>URI contém userinfo (<c>usuário:senha@host</c>) — nunca aceito.</summary>
    UserInfoPresent,

    /// <summary>URI contém fragment (<c>#...</c>) — nunca aceito.</summary>
    FragmentPresent,

    /// <summary>Host fora do domínio de storage Purview/Microsoft autorizado.</summary>
    HostNotAuthorized,

    /// <summary>Caminho não é exatamente o container <c>ingestiondata</c> documentado (runbook §25.5).</summary>
    ContainerNotAuthorized,

    /// <summary>Segmentos de path além do container esperado (path inesperado).</summary>
    UnexpectedPath,

    /// <summary>Algum parâmetro crítico do SAS aparece duplicado na query string.</summary>
    DuplicateCriticalParameter,

    /// <summary>SAS referenciado por identificador de policy nomeada (<c>si</c>) em vez de parâmetros explícitos — permissões/expiry não são verificáveis estaticamente.</summary>
    StoredPolicyReferenceNotVerifiable,

    /// <summary>Parâmetro crítico obrigatório ausente (<c>sv</c>/<c>se</c>/<c>sp</c>/<c>sig</c>).</summary>
    MissingCriticalParameter,

    /// <summary>Parâmetro de expiry (<c>se</c>) malformado (não parseável como data/hora ISO-8601).</summary>
    ExpiryMalformed,

    /// <summary>Expiry já no passado (ou não deixa margem mínima futura) em relação ao instante da validação.</summary>
    ExpiryAlreadyExpiredOrTooSoon,

    /// <summary>Expiry além da janela máxima de validade aceita pela policy do produto.</summary>
    ExpiryExceedsMaximumWindow,

    /// <summary>Parâmetro de permissões (<c>sp</c>) contém letra não reconhecida.</summary>
    PermissionsUnrecognized,

    /// <summary>Permissões não satisfazem a policy mínima/máxima de upload (item 4).</summary>
    PermissionsNotWithinUploadPolicy,

    /// <summary>Protocolo restrito (<c>spr</c>) presente mas não restrito a <c>https</c>.</summary>
    ProtocolRestrictionNotHttpsOnly,
}
