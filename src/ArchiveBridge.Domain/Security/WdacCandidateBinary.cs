using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Identidade de UM binário candidato apresentado para validação contra a allowlist WDAC/App Control
/// (AB-I7-008 item 2) — nunca aplicado a nenhum host real, apenas validado em memória contra
/// <see cref="WdacPolicyEvidence.Validate"/>.
/// </summary>
public readonly record struct WdacCandidateBinary(string? Publisher, Sha256Hash? Sha256, string? Path);
