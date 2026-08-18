using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Modo de Demonstração (Presentation Mode) — uma faceta EXCLUSIVA DE UI para exibir a interface do
/// ArchiveBridge com um conjunto de dados 100% SINTÉTICO quando o ambiente não possui dados reais suficientes
/// (por exemplo, uma demonstração para cliente). É deliberadamente uma opção do próprio Control Plane.
///
/// Garantias inegociáveis (impostas no <see cref="EnsureAllowedOrThrow"/> e no provedor sintético):
/// <list type="bullet">
///   <item>Default versionado é <see langword="false"/> — fail-closed: sem habilitação explícita, o portal
///   usa somente provedores reais.</item>
///   <item>Só pode ser habilitado em <c>Development</c> ou <c>Staging</c>. Habilitá-lo em <c>Production</c>
///   (ou em qualquer ambiente não reconhecido) FALHA o startup — nunca há dados simulados em produção.</item>
///   <item>É estritamente somente leitura: não cria linha em SQL, não enfileira Job, não chama Worker/PowerShell,
///   não executa descoberta, não gera evidência nem tentativa de validação. Isso é imposto separadamente nos
///   handlers de escrita (que recusam quando o modo está ativo) e pela natureza em memória do provedor.</item>
/// </list>
/// Nunca mistura dados reais com sintéticos: quando ativo, as telas leem SOMENTE o dataset sintético; quando
/// inativo, leem SOMENTE os provedores reais.
/// </summary>
public sealed class PresentationModeOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "PresentationMode";

    /// <summary>Quando <see langword="true"/>, o portal serve dados sintéticos de demonstração. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Impõe a regra de ambiente (fail-closed). Se o modo estiver habilitado fora de Development/Staging,
    /// lança <see cref="InvalidOperationException"/> para abortar o startup — garantindo que Produção nunca
    /// suba com dados simulados. Chamado uma única vez na composição, antes de <c>app.Run()</c>.
    /// </summary>
    public void EnsureAllowedOrThrow(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!Enabled)
        {
            return;
        }

        if (!environment.IsDevelopment() && !environment.IsStaging())
        {
            throw new InvalidOperationException(
                $"PresentationMode:Enabled=true é proibido no ambiente '{environment.EnvironmentName}'. " +
                "O modo de demonstração só é permitido em Development ou Staging (fail-closed).");
        }
    }
}
