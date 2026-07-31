using System.Text;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Analisador CSV RFC 4180 estrito (sem dependência de Excel/COM/Office). É <b>fail-closed</b>: ao
/// encontrar estrutura inválida — aspas no meio de campo não citado, texto após o fechamento de um
/// campo citado, aspas não fechadas — lança <see cref="MappingCsvFormatException"/> em vez de tentar
/// reparar. Aspas escapadas (<c>""</c>) e vírgulas/quebras dentro de campos citados são suportadas.
/// Não descarta registros: cada registro do texto vira um registro de saída.
/// </summary>
internal static class MappingCsvParser
{
    private enum State
    {
        FieldStart,
        InUnquoted,
        InQuoted,
        AfterQuote,
    }

    /// <summary>Analisa o texto em registros; cada registro é a lista de campos daquela linha.</summary>
    /// <exception cref="MappingCsvFormatException">Quando o texto é estruturalmente malformado.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = State.FieldStart;
        var recordStarted = false;

        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            switch (state)
            {
                case State.FieldStart:
                    recordStarted = true;
                    if (current == '"')
                    {
                        state = State.InQuoted;
                        index++;
                    }
                    else if (current == ',')
                    {
                        EndField(fields, field);
                        index++;
                    }
                    else if (current is '\r' or '\n')
                    {
                        EndRecord(records, ref fields, field, ref recordStarted, ref state);
                        index += NewlineWidth(text, index);
                    }
                    else
                    {
                        field.Append(current);
                        state = State.InUnquoted;
                        index++;
                    }

                    break;

                case State.InUnquoted:
                    if (current == '"')
                    {
                        throw new MappingCsvFormatException("Aspas no meio de um campo não citado.");
                    }
                    else if (current == ',')
                    {
                        EndField(fields, field);
                        state = State.FieldStart;
                        index++;
                    }
                    else if (current is '\r' or '\n')
                    {
                        EndRecord(records, ref fields, field, ref recordStarted, ref state);
                        index += NewlineWidth(text, index);
                    }
                    else
                    {
                        field.Append(current);
                        index++;
                    }

                    break;

                case State.InQuoted:
                    if (current == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            field.Append('"');
                            index += 2;
                        }
                        else
                        {
                            state = State.AfterQuote;
                            index++;
                        }
                    }
                    else
                    {
                        field.Append(current);
                        index++;
                    }

                    break;

                case State.AfterQuote:
                    if (current == ',')
                    {
                        EndField(fields, field);
                        state = State.FieldStart;
                        index++;
                    }
                    else if (current is '\r' or '\n')
                    {
                        EndRecord(records, ref fields, field, ref recordStarted, ref state);
                        index += NewlineWidth(text, index);
                    }
                    else
                    {
                        throw new MappingCsvFormatException(
                            "Texto após o fechamento de um campo citado (fora de delimitador/quebra).");
                    }

                    break;

                default:
                    throw new InvalidOperationException("Estado de parser inválido.");
            }
        }

        if (state == State.InQuoted)
        {
            throw new MappingCsvFormatException("Aspas não fechadas no fim do arquivo.");
        }

        if (recordStarted || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }

    private static int NewlineWidth(string text, int index) =>
        text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;

    private static void EndField(List<string> fields, StringBuilder field)
    {
        fields.Add(field.ToString());
        field.Clear();
    }

    private static void EndRecord(
        List<IReadOnlyList<string>> records,
        ref List<string> fields,
        StringBuilder field,
        ref bool recordStarted,
        ref State state)
    {
        fields.Add(field.ToString());
        field.Clear();
        records.Add(fields);
        fields = [];
        recordStarted = false;
        state = State.FieldStart;
    }
}
