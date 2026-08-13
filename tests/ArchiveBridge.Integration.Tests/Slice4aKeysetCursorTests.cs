using System.Text;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.ControlPlane.Composition;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Codec do cursor keyset (unitário). Prova o round-trip e o comportamento ESTRITO fail-closed: Base64/JSON
/// inválido, versão desconhecida, posição malformada e fingerprint divergente TODOS resultam em decode falho
/// (o handler HTTP traduz para 400) — nunca exceção. O cursor não carrega escopo; só chaves de ordenação.
/// </summary>
public sealed class Slice4aKeysetCursorTests
{
    private const string Fingerprint = "abc0123456789def";

    [Fact]
    public void EvidencePositionRoundTrips()
    {
        var position = new EvidenceSeekPosition(
            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), Guid.NewGuid(), 7);

        var cursor = KeysetCursor.Encode(Fingerprint, position);
        Assert.True(KeysetCursor.TryDecode<EvidenceSeekPosition>(cursor, Fingerprint, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void AuditPositionRoundTrips()
    {
        var position = new AuditSeekPosition(new DateTimeOffset(2026, 6, 1, 12, 0, 0, 123, TimeSpan.Zero), 4242);

        var cursor = KeysetCursor.Encode(Fingerprint, position);
        Assert.True(KeysetCursor.TryDecode<AuditSeekPosition>(cursor, Fingerprint, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void CursorIsUrlSafe()
    {
        var cursor = KeysetCursor.Encode(Fingerprint, new AuditSeekPosition(DateTimeOffset.UtcNow, 1));
        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
        Assert.DoesNotContain('=', cursor);
    }

    [Fact]
    public void FingerprintMismatchIsRejected()
    {
        var cursor = KeysetCursor.Encode(Fingerprint, new AuditSeekPosition(DateTimeOffset.UtcNow, 9));
        Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(cursor, "0000000000000000", out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void OversizedCursorIsRejectedBeforeDecoding()
    {
        // Muito acima do teto (2048). Deve ser recusado ANTES de qualquer normalização/deserialização — sem
        // exceção. (Caracteres do alfabeto Base64Url para provar que é o TAMANHO, não o alfabeto, que barra.)
        var oversized = new string('A', 4096);
        Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(oversized, Fingerprint, out var decoded));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData("YWJ+")]     // '+' — alfabeto Base64 padrão, NÃO Base64Url
    [InlineData("YWJ/")]     // '/' — idem
    [InlineData("YWJj=")]    // '=' — padding não é cursor canônico
    [InlineData("YW Jj")]    // espaço
    [InlineData("YWJj\n")]   // controle
    public void NonCanonicalBase64UrlAlphabetIsRejected(string cursor)
    {
        // "URL-safe estrito": só A–Z a–z 0–9 - _ são aceitos. '+', '/', '=' e quaisquer outros ⇒ recusa.
        Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(cursor, Fingerprint, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void StandardBase64OfAValidEnvelopeIsRejectedWhenNotUrlSafe()
    {
        // Mesmo payload de um cursor válido, mas em Base64 PADRÃO (com '+'/'/' e padding '='): não é canônico.
        var position = new AuditSeekPosition(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), 7);
        var urlSafe = KeysetCursor.Encode(Fingerprint, position);
        var standard = urlSafe.Replace('-', '+').Replace('_', '/');
        switch (standard.Length % 4)
        {
            case 2: standard += "=="; break;
            case 3: standard += "="; break;
        }

        // O canônico decodifica; a forma padrão (se diferir por conter +/ / ou '=') é recusada.
        Assert.True(KeysetCursor.TryDecode<AuditSeekPosition>(urlSafe, Fingerprint, out _));
        if (!string.Equals(standard, urlSafe, StringComparison.Ordinal))
        {
            Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(standard, Fingerprint, out _));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("YWJj")]                 // Base64 válido mas não é o JSON esperado
    [InlineData("e30")]                  // "{}" — envelope sem campos obrigatórios
    [InlineData("bm90LWpzb24")]          // "not-json"
    public void MalformedCursorsAreRejectedWithoutThrowing(string cursor)
    {
        Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(cursor, Fingerprint, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void UnknownSchemaVersionIsRejected()
    {
        // Envelope com v=99 (versão desconhecida) — deve ser recusado.
        var json = "{\"v\":99,\"f\":\"" + Fingerprint + "\",\"k\":{\"OccurredAtUtc\":\"2026-06-01T00:00:00+00:00\",\"EventId\":5}}";
        var forged = ToBase64Url(Encoding.UTF8.GetBytes(json));
        Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(forged, Fingerprint, out _));
    }

    [Fact]
    public void MalformedPositionIsRejected()
    {
        // Envelope v=1 e fingerprint correto, mas EventId = 0 (posição não bem-formada).
        var json = "{\"v\":1,\"f\":\"" + Fingerprint + "\",\"k\":{\"OccurredAtUtc\":\"2026-06-01T00:00:00+00:00\",\"EventId\":0}}";
        var forged = ToBase64Url(Encoding.UTF8.GetBytes(json));
        Assert.False(KeysetCursor.TryDecode<AuditSeekPosition>(forged, Fingerprint, out _));
    }

    [Fact]
    public void FilterFingerprintIsStableAndSensitive()
    {
        var a = FilterFingerprint.Compute("ev.discovery.request", null, "True", null, null, null);
        var b = FilterFingerprint.Compute("ev.discovery.request", null, "True", null, null, null);
        var c = FilterFingerprint.Compute("evidence.download", null, "True", null, null, null);
        Assert.Equal(a, b);              // determinístico
        Assert.NotEqual(a, c);           // sensível aos filtros
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void FilterFingerprintDistinguishesNullFromEmpty()
    {
        Assert.NotEqual(
            FilterFingerprint.Compute(null, "x"),
            FilterFingerprint.Compute("", "x"));
    }

    [Theory]
    [InlineData(-1, KeysetPaging.MinPageSize)]
    [InlineData(0, KeysetPaging.MinPageSize)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(1_000_000, KeysetPaging.MaxPageSize)]
    [InlineData(int.MaxValue, KeysetPaging.MaxPageSize)]
    public void PageSizeIsClampedDeterministically(int requested, int expected) =>
        Assert.Equal(expected, KeysetPaging.ClampPageSize(requested));

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
