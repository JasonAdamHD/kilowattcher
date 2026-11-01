using Kilowattcher.Api.Rag;

namespace Kilowattcher.Api.Tests.Rag;

public class MarkdownChunkerTests
{
    private const string Sample = """
        # VoltNest 13 Owner's Guide

        Intro text before the first section is not a chunk.

        ## Reserve percentage

        The reserve is kept for outages.
        Default is 20%.

        ## What backup can't power

        Loads over 5 kW.
        """;

    [Fact]
    public void Splits_on_second_level_headings_with_file_based_ids()
    {
        var chunks = MarkdownChunker.Chunk("battery-manual", Sample);

        Assert.Equal(["battery-manual#reserve-percentage", "battery-manual#what-backup-cant-power"], chunks.Select(c => c.Id));
        Assert.All(chunks, c => Assert.Equal("VoltNest 13 Owner's Guide", c.Title));
        Assert.Equal("Reserve percentage", chunks[0].Heading);
        Assert.Equal("The reserve is kept for outages.\nDefault is 20%.", chunks[0].Text);
    }

    [Fact]
    public void Windows_line_endings_give_the_same_chunks()
    {
        // Normalize first: Git may check this test file out with either line ending.
        var lf = Sample.ReplaceLineEndings("\n");
        var unix = MarkdownChunker.Chunk("doc", lf);
        var windows = MarkdownChunker.Chunk("doc", lf.Replace("\n", "\r\n"));

        Assert.Equal(unix.Select(c => (c.Id, c.ContentHash)), windows.Select(c => (c.Id, c.ContentHash)));
    }

    [Theory]
    [InlineData("Reserve percentage", "reserve-percentage")]
    [InlineData("What backup can't power", "what-backup-cant-power")]
    [InlineData("Should I charge my battery from the grid tonight?", "should-i-charge-my-battery-from-the-grid-tonight")]
    [InlineData("A typical day’s production", "a-typical-days-production")]
    [InlineData("  Rate periods & prices  ", "rate-periods-prices")]
    public void Slugs_are_lowercase_and_hyphenated(string heading, string expected)
    {
        Assert.Equal(expected, MarkdownChunker.Slug(heading));
    }

    [Fact]
    public void Duplicate_headings_in_one_file_are_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarkdownChunker.Chunk("faq", "# FAQ\n\n## Same\n\nOne.\n\n## Same\n\nTwo."));

        Assert.Contains("faq#same", ex.Message);
    }

    [Fact]
    public void Empty_sections_are_skipped_and_a_missing_title_falls_back_to_the_file_name()
    {
        var chunks = MarkdownChunker.Chunk("notes", "## Empty\n\n## Filled\n\nSomething.");

        var chunk = Assert.Single(chunks);
        Assert.Equal("notes#filled", chunk.Id);
        Assert.Equal("notes", chunk.Title);
    }

    [Fact]
    public void Content_hash_changes_when_the_text_changes()
    {
        var before = MarkdownChunker.Chunk("doc", "# T\n\n## H\n\nDefault is 20%.")[0];
        var after = MarkdownChunker.Chunk("doc", "# T\n\n## H\n\nDefault is 25%.")[0];

        Assert.NotEqual(before.ContentHash, after.ContentHash);
    }
}
