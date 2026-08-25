using System.Text;
using OpsFlow.Application.Documents;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Extracts text from plain-text documents (text/plain). Uses strict UTF-8
/// decoding — malformed byte sequences are rejected, not silently replaced.
/// </summary>
public sealed class PlainTextExtractor : IDocumentTextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string canonicalContentType) =>
        string.Equals(canonicalContentType, "text/plain", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DocumentTextExtractionResult> ExtractAsync(
        Stream content,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var encoding = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        try
        {
            using var reader = new StreamReader(
                content, encoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 8192,
                leaveOpen: true);

            var sb = new StringBuilder();
            var buffer = new char[8192];
            bool firstChunk = true;
            bool pendingCr = false;

            int charsRead;
            while ((charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                int start = 0;
                if (firstChunk && charsRead > 0 && buffer[0] == '﻿')
                {
                    start = 1;
                }

                firstChunk = false;

                for (int i = start; i < charsRead; i++)
                {
                    char c = buffer[i];

                    if (pendingCr)
                    {
                        pendingCr = false;
                        _ = sb.Append('\n');

                        if (c == '\n')
                        {
                            continue;
                        }

                        if (c == '\r')
                        {
                            pendingCr = true;
                            continue;
                        }

                        if (c == '\0')
                        {
                            continue;
                        }

                        _ = sb.Append(c);
                        continue;
                    }

                    if (c == '\r')
                    {
                        pendingCr = true;
                        continue;
                    }

                    if (c == '\0')
                    {
                        continue;
                    }

                    _ = sb.Append(c);
                }

                if (sb.Length > maxCharacters)
                {
                    return DocumentTextExtractionResult.LimitExceeded();
                }
            }

            if (pendingCr)
            {
                _ = sb.Append('\n');
            }

            if (sb.Length > maxCharacters)
            {
                return DocumentTextExtractionResult.LimitExceeded();
            }

            return DocumentTextExtractionResult.Success(sb.ToString().Trim());
        }
        catch (DecoderFallbackException)
        {
            return DocumentTextExtractionResult.MalformedDocument();
        }
    }
}
