using System.Text;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Analisador CSV RFC 4180 (sem dependência de Excel/COM/Office) usado para validar um CSV externo
/// campo a campo. Suporta aspas, aspas escapadas (<c>""</c>) e vírgulas/quebras dentro de campos
/// citados. Não descarta registros silenciosamente: cada registro do texto vira um registro de saída.
/// </summary>
internal static class MappingCsvParser
{
    /// <summary>Analisa o texto em registros; cada registro é a lista de campos daquela linha.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordHasContent = false;

        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index += 2;
                        continue;
                    }

                    inQuotes = false;
                    index++;
                    continue;
                }

                field.Append(current);
                index++;
                continue;
            }

            switch (current)
            {
                case '"':
                    inQuotes = true;
                    recordHasContent = true;
                    index++;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    recordHasContent = true;
                    index++;
                    break;
                case '\r':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(fields);
                    fields = [];
                    recordHasContent = false;
                    index++;
                    if (index < text.Length && text[index] == '\n')
                    {
                        index++;
                    }

                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(fields);
                    fields = [];
                    recordHasContent = false;
                    index++;
                    break;
                default:
                    field.Append(current);
                    recordHasContent = true;
                    index++;
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0 || recordHasContent)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }
}
