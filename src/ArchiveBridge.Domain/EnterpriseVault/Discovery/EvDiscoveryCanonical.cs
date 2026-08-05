using System.Buffers.Binary;
using System.Text;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Codificação canônica TIPADA e UNÍVOCA da evidência semântica de uma execução de descoberta, base do
/// <see cref="EvDiscoverySemanticFingerprint"/>. NÃO concatena strings por separador: cada campo é escrito
/// com um <b>tipo</b> explícito, uma <b>tag</b>, um <b>marcador de presença</b> para os anuláveis, e cada
/// string é gravada como <b>comprimento UTF-8 + bytes UTF-8</b> (length-prefixed) — de modo que qualquer
/// valor (inclusive contendo <c>U+001F</c>) é representado sem ambiguidade e sem sentinela. Listas gravam a
/// <b>quantidade</b> explícita; inteiros e enums vão em binário big-endian com tipo. Ausência NUNCA é
/// representada por um valor que também exista no domínio (<c>null</c> ≠ <c>""</c> ≠ <c>0</c> ≠
/// <c>"&lt;none&gt;"</c>). A codificação valida as invariantes de unicidade antes de emitir bytes; como
/// avaliações são únicas por <see cref="EvAdapterId"/> e capacidades por <see cref="EvCapabilityCode"/>, a
/// ordenação é ordinal por essas chaves (sem chave composta concatenada e sem depender da estabilidade do
/// <c>OrderBy</c> para chaves iguais).
/// </summary>
public static class EvDiscoveryCanonical
{
    /// <summary>Versão do formato de codificação canônica (parte dos bytes, para evolução segura).</summary>
    public const long FormatVersion = 1;

    /// <summary>Produz os bytes canônicos determinísticos do resultado (após validar as invariantes).</summary>
    public static byte[] Encode(EvDiscoveryRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EvDiscoveryInvariants.Validate(result); // fail-closed ANTES de codificar

        var writer = new CanonicalWriter();
        writer.Field("evdFormat");
        writer.Int(FormatVersion);
        writer.Field("schema");
        writer.Int(result.CapabilitySet.DiscoverySchemaVersion);
        writer.Field("status");
        writer.Int((long)result.Status);
        writer.Field("resultCode");
        writer.Str(result.ResultCode.Value);

        writer.Field("envId");
        writer.Str(result.Environment.EnvironmentId.Value.ToString("N"));
        writer.Field("site");
        writer.Str(result.Environment.SiteName);
        writer.Field("server");
        writer.Str(result.Environment.DirectoryServer);
        writer.Field("observed");
        writer.Str(result.Environment.ObservedVersion);
        writer.Field("product");
        writer.Str(result.Environment.ProductVersion);
        writer.Field("source");
        writer.Str(result.Environment.DiscoverySource);

        // Adapter do capability set: id e versão ANULÁVEIS com presença explícita (null ≠ "<none>" ≠ 0).
        writer.Field("capabilitySetAdapterId");
        writer.NullableStr(result.CapabilitySet.AdapterId?.Value);
        writer.Field("capabilitySetAdapterVersion");
        writer.NullableInt(result.CapabilitySet.AdapterVersion);

        var capabilities = OrderCapabilities(result.CapabilitySet.Capabilities);
        writer.List("capabilities", capabilities.Count);
        foreach (var capability in capabilities)
        {
            EncodeCapability(writer, capability);
        }

        writer.Field("signature");
        writer.Presence(result.Signature is not null);
        if (result.Signature is { } signature)
        {
            EncodeSignature(writer, signature);
        }

        writer.Field("selectionOutcome");
        writer.Int((long)result.Selection.Outcome);
        writer.Field("selectedAdapterId");
        writer.NullableStr(result.Selection.Selected?.AdapterId.Value);

        var candidates = OrderCandidates(result.Selection.Candidates);
        writer.List("candidates", candidates.Count);
        foreach (var candidate in candidates)
        {
            writer.Field("candidate");
            writer.Str(candidate);
        }

        var selectionFindings = OrderFindings(result.Selection.Findings);
        writer.List("selectionFindings", selectionFindings.Count);
        foreach (var finding in selectionFindings)
        {
            EncodeFinding(writer, finding);
        }

        var evaluations = OrderEvaluations(result.Selection.Evaluations);
        writer.List("adapterEvaluations", evaluations.Count);
        foreach (var evaluation in evaluations)
        {
            EncodeEvaluation(writer, evaluation);
        }

        var blockingFindings = OrderFindings(result.BlockingFindings);
        writer.List("blockingFindings", blockingFindings.Count);
        foreach (var finding in blockingFindings)
        {
            EncodeFinding(writer, finding);
        }

        return writer.ToArray();
    }

    // Avaliações são ÚNICAS por AdapterId e capacidades por CapabilityCode (invariantes): a ordenação ordinal
    // por essas chaves é total. Expostas para o serializer usar a MESMA ordem.

    /// <summary>Capacidades ordenadas por <see cref="EvCapabilityCode"/> (único no conjunto).</summary>
    public static IReadOnlyList<EvCapability> OrderCapabilities(IEnumerable<EvCapability> capabilities) =>
        [.. capabilities.OrderBy(static c => c.CapabilityCode.Value, StringComparer.Ordinal)];

    /// <summary>Avaliações ordenadas por <see cref="EvAdapterId"/> (único na execução).</summary>
    public static IReadOnlyList<EvAdapterEvaluation> OrderEvaluations(IEnumerable<EvAdapterEvaluation> evaluations) =>
        [.. evaluations.OrderBy(static e => e.AdapterId.Value, StringComparer.Ordinal)];

    /// <summary>Candidatos (AdapterIds) ordenados ordinalmente.</summary>
    public static IReadOnlyList<string> OrderCandidates(IEnumerable<EvAdapterId> candidates) =>
        [.. candidates.Select(static c => c.Value).OrderBy(static v => v, StringComparer.Ordinal)];

    /// <summary>Requisitos ordenados por (código, descrição) — não têm invariante de unicidade.</summary>
    public static IReadOnlyList<EvAdapterRequirement> OrderRequirements(IEnumerable<EvAdapterRequirement> requirements) =>
        [.. requirements
            .OrderBy(static r => r.CapabilityCode.Value, StringComparer.Ordinal)
            .ThenBy(static r => r.Description, StringComparer.Ordinal)];

    /// <summary>
    /// Achados em ordem TOTAL por todos os campos (sem invariante de unicidade). A presença do
    /// <see cref="EvDiscoveryFinding.CapabilityCode"/> entra na chave (ausente ordena antes de presente),
    /// então dois achados idênticos exceto por presença de capacidade não empatam.
    /// </summary>
    public static IReadOnlyList<EvDiscoveryFinding> OrderFindings(IEnumerable<EvDiscoveryFinding> findings) =>
        [.. findings
            .OrderBy(static f => f.ResultCode.Value, StringComparer.Ordinal)
            .ThenBy(static f => f.CapabilityCode is null ? 0 : 1)
            .ThenBy(static f => f.CapabilityCode?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static f => f.Reason, StringComparer.Ordinal)
            .ThenBy(static f => (int)f.ErrorCategory)];

    private static void EncodeCapability(CanonicalWriter writer, EvCapability capability)
    {
        writer.Field("capabilityCode");
        writer.Str(capability.CapabilityCode.Value);
        writer.Field("capabilityVersion");
        writer.Int(capability.CapabilityVersion);
        writer.Field("availability");
        writer.Int((long)capability.Availability);
        writer.Field("evidenceReference");
        writer.Str(capability.EvidenceReference);
        writer.Field("blockingReason");
        writer.NullableStr(capability.BlockingReason);
    }

    private static void EncodeFinding(CanonicalWriter writer, EvDiscoveryFinding finding)
    {
        writer.Field("findingResultCode");
        writer.Str(finding.ResultCode.Value);
        writer.Field("findingCapabilityCode");
        writer.NullableStr(finding.CapabilityCode?.Value);
        writer.Field("findingReason");
        writer.Str(finding.Reason);
        writer.Field("findingErrorCategory");
        writer.Int((long)finding.ErrorCategory);
    }

    private static void EncodeSignature(CanonicalWriter writer, EvExportSignature signature)
    {
        writer.Field("signatureHash");
        writer.Str(signature.SignatureHash.Value);
        writer.Field("signatureCommandName");
        writer.Str(signature.CommandName);
        writer.Field("signatureModuleSource");
        writer.Str(signature.ModuleSource);
        writer.Field("signatureCommandType");
        writer.Str(signature.CommandType);
        WriteStringList(writer, "signatureParameters", signature.Parameters);
        WriteStringList(writer, "signatureMandatoryParameters", signature.MandatoryParameters);
        WriteStringList(writer, "signatureParameterSets", signature.ParameterSets);
    }

    private static void EncodeEvaluation(CanonicalWriter writer, EvAdapterEvaluation evaluation)
    {
        writer.Field("adapterId");
        writer.Str(evaluation.AdapterId.Value);
        writer.Field("adapterVersion");
        writer.Int(evaluation.AdapterVersion);
        writer.Field("compatibility");
        writer.Int((long)evaluation.Compatibility);
        writer.Field("precedence");
        writer.Int(evaluation.Precedence);
        writer.Field("profileId");
        writer.NullableStr(evaluation.ProfileId);

        writer.Field("maturity");
        writer.Presence(evaluation.Maturity is not null);
        if (evaluation.Maturity is { } maturity)
        {
            writer.Field("runtimeObserved");
            writer.Bool(maturity.RuntimeObserved);
            writer.Field("officialDocumentation");
            writer.Bool(maturity.OfficialDocumentation);
            writer.Field("automatedFixtureValidated");
            writer.Bool(maturity.AutomatedFixtureValidated);
            writer.Field("laboratoryValidated");
            writer.Bool(maturity.LaboratoryValidated);
        }

        var requirements = OrderRequirements(evaluation.Requirements);
        writer.List("requirements", requirements.Count);
        foreach (var requirement in requirements)
        {
            writer.Field("requirementCode");
            writer.Str(requirement.CapabilityCode.Value);
            writer.Field("requirementDescription");
            writer.Str(requirement.Description);
        }

        var declaredCapabilities = OrderCapabilities(evaluation.Capabilities);
        writer.List("declaredCapabilities", declaredCapabilities.Count);
        foreach (var capability in declaredCapabilities)
        {
            EncodeCapability(writer, capability);
        }

        var findings = OrderFindings(evaluation.Findings);
        writer.List("adapterFindings", findings.Count);
        foreach (var finding in findings)
        {
            EncodeFinding(writer, finding);
        }
    }

    private static void WriteStringList(CanonicalWriter writer, string tag, IReadOnlyList<string> values)
    {
        writer.List(tag, values.Count);
        foreach (var value in values)
        {
            writer.Field("item");
            writer.Str(value);
        }
    }

    /// <summary>
    /// Escritor canônico: cada elemento carrega um byte de TIPO e as strings são length-prefixed (UTF-8).
    /// Sem separador e sem sentinela — a saída é unívoca por construção.
    /// </summary>
    private sealed class CanonicalWriter
    {
        private const byte TypeTag = (byte)'t';
        private const byte TypeString = (byte)'s';
        private const byte TypeInt = (byte)'i';
        private const byte TypeBool = (byte)'b';
        private const byte TypePresence = (byte)'p';

        private readonly List<byte> _buffer = [];

        public void Field(string tag) => WriteLengthPrefixed(TypeTag, Encoding.UTF8.GetBytes(tag));

        public void Str(string value) => WriteLengthPrefixed(TypeString, Encoding.UTF8.GetBytes(value));

        public void NullableStr(string? value)
        {
            Presence(value is not null);
            if (value is not null)
            {
                Str(value);
            }
        }

        public void Int(long value)
        {
            _buffer.Add(TypeInt);
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            _buffer.AddRange(bytes);
        }

        public void NullableInt(int? value)
        {
            Presence(value.HasValue);
            if (value.HasValue)
            {
                Int(value.Value);
            }
        }

        public void Bool(bool value)
        {
            _buffer.Add(TypeBool);
            _buffer.Add(value ? (byte)1 : (byte)0);
        }

        public void Presence(bool present)
        {
            _buffer.Add(TypePresence);
            _buffer.Add(present ? (byte)1 : (byte)0);
        }

        public void List(string tag, int count)
        {
            Field(tag);
            Int(count);
        }

        public byte[] ToArray() => [.. _buffer];

        private void WriteLengthPrefixed(byte type, byte[] bytes)
        {
            _buffer.Add(type);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            _buffer.AddRange(length);
            _buffer.AddRange(bytes);
        }
    }
}
