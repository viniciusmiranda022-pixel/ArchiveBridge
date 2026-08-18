using Microsoft.AspNetCore.Html;

namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Conjunto de ícones SVG INLINE (traço, 24×24, <c>currentColor</c>). São markup no DOM — não recursos
/// externos — portanto compatíveis com a CSP restrita (sem fonte de ícones, sem CDN, sem &lt;img&gt; remoto).
/// Centraliza a iconografia para consistência visual entre páginas e partials.
/// </summary>
public static class Icons
{
    private const string Open =
        "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" " +
        "stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["dashboard"] = "<rect x='3' y='3' width='7' height='9' rx='1'/><rect x='14' y='3' width='7' height='5' rx='1'/><rect x='14' y='12' width='7' height='9' rx='1'/><rect x='3' y='16' width='7' height='5' rx='1'/>",
        ["projects"] = "<path d='M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/>",
        ["waves"] = "<path d='M2 6c2.5 2 4.5 2 7 0s4.5-2 7 0 4.5 2 6 0'/><path d='M2 12c2.5 2 4.5 2 7 0s4.5-2 7 0 4.5 2 6 0'/><path d='M2 18c2.5 2 4.5 2 7 0s4.5-2 7 0 4.5 2 6 0'/>",
        ["vault"] = "<rect x='3' y='4' width='18' height='16' rx='2'/><circle cx='12' cy='12' r='4'/><path d='M12 8v1M12 15v1M8 12h1M15 12h1'/>",
        ["mapping"] = "<path d='M4 6h7M4 12h7M4 18h7'/><path d='M15 6h5M15 12h5M15 18h5'/><circle cx='12.5' cy='6' r='1.4'/><circle cx='12.5' cy='12' r='1.4'/><circle cx='12.5' cy='18' r='1.4'/>",
        ["evidence"] = "<path d='M9 4h6l4 4v11a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z'/><path d='M14 4v4h4'/><path d='M9 13l2 2 4-4'/>",
        ["audit"] = "<path d='M4 5a1 1 0 0 1 1-1h10l5 5v10a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1z'/><path d='M15 4v5h5'/><path d='M8 13h6M8 16h4'/>",
        ["jobs"] = "<circle cx='12' cy='12' r='3'/><path d='M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4'/>",
        ["settings"] = "<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1V21a2 2 0 1 1-4 0v-.2A1.6 1.6 0 0 0 6.6 19a2 2 0 1 1-2.8-2.8l.1-.1A1.6 1.6 0 0 0 3.2 13H3a2 2 0 1 1 0-4h.2A1.6 1.6 0 0 0 5 6.6a2 2 0 1 1 2.8-2.8l.1.1A1.6 1.6 0 0 0 11 3.2V3a2 2 0 1 1 4 0v.2a1.6 1.6 0 0 0 2.7 1.1l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1A1.6 1.6 0 0 0 20.8 11H21a2 2 0 1 1 0 4h-.2a1.6 1.6 0 0 0-1.4.9z'/>",
        ["check"] = "<path d='M20 6L9 17l-5-5'/>",
        ["check-circle"] = "<circle cx='12' cy='12' r='9'/><path d='M8.5 12.5l2.5 2.5 4.5-5'/>",
        ["clock"] = "<circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/>",
        ["alert"] = "<path d='M12 3l9 16H3z'/><path d='M12 10v4M12 17h.01'/>",
        ["block"] = "<circle cx='12' cy='12' r='9'/><path d='M6 6l12 12'/>",
        ["x-circle"] = "<circle cx='12' cy='12' r='9'/><path d='M9 9l6 6M15 9l-6 6'/>",
        ["copy"] = "<rect x='9' y='9' width='11' height='11' rx='2'/><path d='M5 15V5a2 2 0 0 1 2-2h8'/>",
        ["download"] = "<path d='M12 3v12M7 11l5 5 5-5'/><path d='M5 21h14'/>",
        ["upload"] = "<path d='M12 20V8M7 11l5-5 5 5'/><path d='M5 4h14'/>",
        ["shield"] = "<path d='M12 3l7 3v5c0 5-3.5 8-7 10-3.5-2-7-5-7-10V6z'/>",
        ["database"] = "<ellipse cx='12' cy='5' rx='8' ry='3'/><path d='M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5'/><path d='M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3'/>",
        ["plug"] = "<path d='M9 3v6M15 3v6'/><path d='M6 9h12v3a6 6 0 0 1-12 0z'/><path d='M12 18v3'/>",
        ["menu"] = "<path d='M4 6h16M4 12h16M4 18h16'/>",
        ["logout"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><path d='M16 17l5-5-5-5M21 12H9'/>",
        ["info"] = "<circle cx='12' cy='12' r='9'/><path d='M12 11v5M12 8h.01'/>",
        ["arrow-left"] = "<path d='M19 12H5M12 19l-7-7 7-7'/>",
        ["file"] = "<path d='M9 4h6l4 4v11a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z'/><path d='M14 4v4h4'/>",
        ["inbox"] = "<path d='M4 13l2.5-8h11L20 13'/><path d='M4 13v5a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-5h-5a3 3 0 0 1-6 0z'/>",
        ["bridge"] = "<path d='M3 8v8M21 8v8M3 12c3 0 3-4 6-4s3 4 6 4 3-4 6-4M3 16h18'/>",
        ["cloud"] = "<path d='M7 18a4 4 0 0 1 0-8 5 5 0 0 1 9.6-1.4A3.5 3.5 0 0 1 18 18z'/>",
        ["layers"] = "<path d='M12 3l9 5-9 5-9-5z'/><path d='M3 12l9 5 9-5'/>",
        ["clipboard-check"] = "<rect x='6' y='4' width='12' height='17' rx='2'/><path d='M9 4a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2'/><path d='M9 13l2 2 4-4'/>",
        ["search"] = "<circle cx='11' cy='11' r='7'/><path d='M21 21l-4.3-4.3'/>",
    };

    /// <summary>Retorna o SVG inline do ícone nomeado (ou uma cadeia vazia se desconhecido).</summary>
    public static IHtmlContent Svg(string name)
    {
        if (!Paths.TryGetValue(name, out var body))
        {
            return HtmlString.Empty;
        }

        return new HtmlString(Open + body + "</svg>");
    }
}
