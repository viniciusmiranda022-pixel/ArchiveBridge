using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Medição REAL de UM controle de hardening — nunca um valor alegado por configuração/documentação sem
/// verificação (AB-I7-008 item 1: "ausência de evidência nunca vira Pass"). Este ambiente sandbox não pode
/// fornecer uma medição real de host Windows nenhum: nenhum código deste Passo constrói esta medição fora
/// de testes, e a ausência dela é exatamente o que mantém os registros de baseline em
/// <see cref="WorkerHardeningStatus.NotMeasured"/>/<see cref="WorkerHardeningStatus.Blocked"/>.
/// </summary>
public readonly record struct WorkerHardeningMeasurement
{
    private const int MethodMaxLength = 200;

    /// <summary>Cria a medição a partir do instante real e do método/mecanismo real de verificação.</summary>
    /// <exception cref="ArgumentException"><paramref name="measurementMethod"/> vazio/inválido.</exception>
    public WorkerHardeningMeasurement(DateTimeOffset measuredAtUtc, string measurementMethod)
    {
        MeasuredAtUtc = measuredAtUtc;
        MeasurementMethod = TextValue.Require(measurementMethod, nameof(measurementMethod), MethodMaxLength);
    }

    /// <summary>Instante real em que a verificação foi executada.</summary>
    public DateTimeOffset MeasuredAtUtc { get; }

    /// <summary>Mecanismo/método real de verificação (ex.: "local policy query via WMI", nunca um caminho/segredo).</summary>
    public string MeasurementMethod { get; }
}
