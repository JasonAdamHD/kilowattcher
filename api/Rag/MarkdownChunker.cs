using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kilowattcher.Api.Rag;

/// <summary>One searchable passage: a "##" section of a doc.</summary>
public sealed record DocChunk(string Id, string Title, string Heading, string Text)
{
    /// <summary>What gets embedded: title and heading give the vector context the body alone lacks.</summary>
    public string EmbeddingInput => $"{Title} — {Heading}\n\n{Text}";

    /// <summary>Detects docs that changed after embeddings.json was built.</summary>
    public string ContentHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddingInput)));
}

/// <summary>
/// Splits markdown docs into chunks, one per "##" heading. Chunk IDs are "{file name}#{heading slug}",
/// for example "battery-manual#reserve-percentage". The embed script and the API share this code,
/// so IDs always match.
/// </summary>
public static partial class MarkdownChunker
{
    public static IReadOnlyList<DocChunk> ChunkDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.md")
            .Order(StringComparer.Ordinal)
            .SelectMany(path => Chunk(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)))
            .ToList();

    public static IReadOnlyList<DocChunk> Chunk(string docName, string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? docName;

        var chunks = new List<DocChunk>();
        string? heading = null;
        var body = new StringBuilder();

        void Flush()
        {
            var text = body.ToString().Trim();
            if (heading is not null && text.Length > 0)
            {
                chunks.Add(new DocChunk($"{docName}#{Slug(heading)}", title, heading, text));
            }

            body.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
            }
            else if (heading is not null && !line.StartsWith("# ", StringComparison.Ordinal))
            {
                // Always "\n", never Environment.NewLine, so hashes match on Windows and Linux.
                body.Append(line).Append('\n');
            }
        }

        Flush();

        var duplicate = chunks.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two sections in {docName}.md share the ID '{duplicate.Key}'. Give them different headings.");
        }

        return chunks;
    }

    /// <summary>"What backup can't power" → "what-backup-cant-power".</summary>
    public static string Slug(string heading)
    {
        var lower = heading.ToLowerInvariant().Replace("'", "").Replace("’", "");
        return NonAlphanumeric().Replace(lower, "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
