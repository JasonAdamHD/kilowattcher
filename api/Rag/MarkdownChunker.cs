using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kilowattcher.Api.Rag;

/// <summary>
/// One searchable passage: a "##" section of a doc. <see cref="Product"/> comes from the doc's front matter
/// ("product: voltnest-13"); null means the passage applies to every household.
/// </summary>
public sealed record DocChunk(string Id, string Title, string Heading, string Text, string? Product = null)
{
    /// <summary>What gets embedded: title, heading, and product give the vector context the body alone lacks.</summary>
    public string EmbeddingInput => Product is null
        ? $"{Title} — {Heading}\n\n{Text}"
        : $"{Title} — {Heading} (product: {Product})\n\n{Text}";

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
        var (product, bodyStart) = ReadFrontMatter(lines);
        lines = lines[bodyStart..];
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? docName;

        var chunks = new List<DocChunk>();
        string? heading = null;
        var body = new StringBuilder();

        void Flush()
        {
            var text = body.ToString().Trim();
            if (heading is not null && text.Length > 0)
            {
                chunks.Add(new DocChunk($"{docName}#{Slug(heading)}", title, heading, text, product));
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

    /// <summary>
    /// Optional front matter at the very top of a doc:
    /// <code>
    /// ---
    /// product: voltnest-13
    /// ---
    /// </code>
    /// Returns the product (or null) and the index of the first body line.
    /// </summary>
    internal static (string? Product, int BodyStart) ReadFrontMatter(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (null, 0);
        }

        string? product = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "---")
            {
                return (product, i + 1);
            }

            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim() == "product")
            {
                product = line[(colon + 1)..].Trim();
                product = product.Length == 0 ? null : product;
            }
        }

        throw new InvalidOperationException("Front matter starts with --- but never closes with ---.");
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
