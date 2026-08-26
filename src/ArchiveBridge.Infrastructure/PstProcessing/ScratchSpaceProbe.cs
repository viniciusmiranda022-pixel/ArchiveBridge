namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Sonda de espaço livre disponível na raiz de volume de um caminho (AB-I7-004 blocker 2) — seam de
/// testabilidade para <see cref="LocalSinglePartExecutionWriter"/>: <see cref="DriveInfo"/> é uma classe
/// selada do runtime, então esta interface é o único jeito de provar deterministicamente, em teste, os
/// vereditos "espaço exatamente suficiente", "1 byte abaixo" e "indeterminável" sem depender do espaço
/// livre REAL do disco onde os testes rodam.
/// </summary>
internal interface IScratchSpaceProbe
{
    /// <summary>
    /// Bytes livres disponíveis na raiz do volume de <paramref name="rootPath"/>, ou <see langword="null"/>
    /// quando não determinável (raiz de volume irresolvível ou drive não pronto) — <see langword="null"/> é
    /// sempre tratado pelo chamador como "desconhecido", nunca como "sem limite"/"suficiente".
    /// </summary>
    long? AvailableFreeSpaceBytes(string rootPath);
}

/// <summary>Implementação real (produção) de <see cref="IScratchSpaceProbe"/> — consulta <see cref="DriveInfo"/> de fato.</summary>
internal sealed class PhysicalScratchSpaceProbe : IScratchSpaceProbe
{
    /// <summary>Instância única — sem estado, seguro para reuso concorrente.</summary>
    public static readonly PhysicalScratchSpaceProbe Instance = new();

    private PhysicalScratchSpaceProbe()
    {
    }

    /// <inheritdoc />
    public long? AvailableFreeSpaceBytes(string rootPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(rootPath));
        if (string.IsNullOrEmpty(root))
        {
            // Sem raiz de volume resolvível (ex.: caminho relativo malformado) — indeterminável.
            return null;
        }

        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
