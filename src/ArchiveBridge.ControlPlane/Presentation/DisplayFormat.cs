using System.Globalization;

namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Formatação de exibição para a UI. Puramente apresentacional (nunca altera dados). Volumes usam base
/// DECIMAL (1 GB = 10^9 bytes), coerente com a regra de capacidade da plataforma (100 GB decimais).
/// </summary>
public static class DisplayFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Volume legível por humanos (ex.: <c>125 GB</c>, <c>1.8 TB</c>). Base decimal.</summary>
    public static string HumanBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        if (bytes < 1000)
        {
            return bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        var format = value >= 100 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>Prefixo abreviado de um hash/identificador longo para exibição (ex.: <c>9f81c2a4…</c>).</summary>
    public static string ShortHash(string? hash, int length = 10)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "—";
        }

        return hash.Length <= length ? hash : hash[..length] + "…";
    }

    /// <summary>Instante em UTC no formato curto e determinístico usado pela plataforma.</summary>
    public static string Utc(DateTimeOffset moment) => moment.ToString("u", CultureInfo.InvariantCulture);
}
