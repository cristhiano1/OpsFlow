namespace OpsFlow.Application.Documents;

/// <summary>
/// Shared deterministic text normalization applied to all extraction output
/// before persistence. Normalizes line endings and removes NUL characters.
/// </summary>
public static class TextNormalization
{
    /// <summary>
    /// Normalizes extracted text: CRLF and CR to LF, removes NUL characters,
    /// trims outer leading and trailing whitespace. Internal paragraph and
    /// page boundaries are preserved.
    /// </summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (c == '\r')
            {
                _ = sb.Append('\n');
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '\0')
            {
                continue;
            }

            _ = sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
