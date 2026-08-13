using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Codec do cursor de paginação keyset — a única camada que conhece Base64Url/JSON de UI. Um cursor é um
/// envelope VERSIONADO e URL-safe que carrega apenas a posição de seek (chaves de ordenação) e o fingerprint
/// dos filtros que o geraram. O cursor NÃO é autorização: nunca contém tenant/projeto, papel, usuário, SQL,
/// caminho físico ou segredo, e jamais troca o escopo (o servidor sempre re-resolve o escopo do principal).
/// A decodificação é estrita e fail-closed: Base64/JSON inválido, versão desconhecida, posição malformada ou
/// fingerprint divergente resultam em <see langword="false"/> (o handler responde 400) — nunca 500, e nenhuma
/// query executa com valores parcialmente parseados.
/// </summary>
public static class KeysetCursor
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Codifica a posição de seek + o fingerprint dos filtros num cursor URL-safe.</summary>
    public static string Encode<TPosition>(string filterFingerprint, TPosition position)
        where TPosition : class, IKeysetPosition
    {
        ArgumentNullException.ThrowIfNull(position);
        var envelope = new CursorEnvelope
        {
            V = SchemaVersion,
            F = filterFingerprint,
            K = JsonSerializer.SerializeToElement(position, Json),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        return ToBase64Url(json);
    }

    /// <summary>
    /// Decodifica ESTRITAMENTE um cursor. Retorna <see langword="false"/> (fail-closed) se o cursor for
    /// inválido de qualquer forma ou se o fingerprint não casar com <paramref name="expectedFingerprint"/>.
    /// </summary>
    public static bool TryDecode<TPosition>(string? cursor, string expectedFingerprint, out TPosition? position)
        where TPosition : class, IKeysetPosition
    {
        position = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            if (!TryFromBase64Url(cursor, out var bytes))
            {
                return false;
            }

            var envelope = JsonSerializer.Deserialize<CursorEnvelope>(bytes, Json);
            if (envelope is null || envelope.V != SchemaVersion || envelope.F is null)
            {
                return false;
            }

            // O fingerprint não é segredo — a comparação ordinal basta como proteção contra mistura de estados.
            if (!string.Equals(envelope.F, expectedFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            var decoded = envelope.K.Deserialize<TPosition>(Json);
            if (decoded is null || !decoded.IsWellFormed())
            {
                return false;
            }

            position = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
            case 1: return false; // comprimento Base64 impossível
        }

        Span<byte> buffer = new byte[(normalized.Length / 4) * 3];
        if (!Convert.TryFromBase64String(normalized, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written].ToArray();
        return true;
    }

    private sealed class CursorEnvelope
    {
        [JsonPropertyName("v")] public int V { get; set; }

        [JsonPropertyName("f")] public string? F { get; set; }

        [JsonPropertyName("k")] public JsonElement K { get; set; }
    }
}

/// <summary>
/// Fingerprint canônico e determinístico dos filtros normalizados de uma consulta paginada. Vincula um cursor
/// aos filtros que o geraram: reaproveitar um cursor com filtros diferentes produz mismatch (o handler
/// responde 400). NÃO é segredo — é apenas proteção contra mistura acidental/maliciosa de estados de paginação.
/// A codificação é length-prefixed (cada parte precedida do seu comprimento), o que elimina qualquer
/// ambiguidade de delimitador sem depender de caractere separador reservado.
/// </summary>
public static class FilterFingerprint
{
    /// <summary>Calcula o fingerprint (hex de 16 chars do SHA-256) das partes normalizadas dos filtros.</summary>
    public static string Compute(params string?[] normalizedParts)
    {
        ArgumentNullException.ThrowIfNull(normalizedParts);
        var builder = new StringBuilder();
        foreach (var part in normalizedParts)
        {
            if (part is null)
            {
                builder.Append("-1:");
            }
            else
            {
                builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
