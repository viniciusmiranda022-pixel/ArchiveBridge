namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>Variante de formato PST identificada pelo cabeçalho (persistido como <c>TINYINT</c>).</summary>
public enum PstFormatVariant
{
    /// <summary>Assinatura reconhecida mas variante não classificada (diagnóstico não é <c>Valid</c>).</summary>
    Unknown = 0,

    /// <summary>PST ANSI legado (Outlook 97-2002, <c>wVer</c> 14/15).</summary>
    AnsiLegacy = 1,

    /// <summary>PST Unicode (Outlook 2003-2010, <c>wVer</c> 23).</summary>
    Unicode2003To2010 = 2,

    /// <summary>PST Unicode 4K (Outlook 2013+, <c>wVer</c> 36/37).</summary>
    Unicode2013Plus = 3,
}
