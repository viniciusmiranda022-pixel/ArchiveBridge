namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>Modelo do cabeçalho de página (título + subtítulo opcional).</summary>
public sealed record PageHeaderModel(string Title, string? Subtitle = null);

/// <summary>Modelo de estado vazio (ícone + título + mensagem explicativa).</summary>
public sealed record EmptyStateModel(string Title, string Message, string Icon = "inbox");

/// <summary>Modelo de exibição de hash abreviado com botão de cópia.</summary>
public sealed record HashModel(string Value, int Length = 10);
