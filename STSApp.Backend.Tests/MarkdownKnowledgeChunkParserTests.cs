using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Tests;

public sealed class MarkdownKnowledgeChunkParserTests
{
    [Fact]
    public void Parse_voiceLink_documents_creates_26_chunks_and_excludes_readme()
    {
        var parser = new MarkdownKnowledgeChunkParser();
        var directory = Path.Combine(AppContext.BaseDirectory, "KnowledgeBase");
        var files = Directory
            .EnumerateFiles(directory, "*.md")
            .Where(path => !string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path)
            .ToArray();

        var documents = files
            .Select(path => parser.Parse(Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

        Assert.Equal(5, documents.Length);
        Assert.Equal(26, documents.Sum(document => document.Chunks.Count));
        Assert.DoesNotContain(documents, document => document.SourcePath.Equals("README.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_uses_level_three_headings_when_they_exist_under_level_two()
    {
        const string markdown = """
            # 資料

            ## 機能
            ### A
            Aの本文
            ### B
            Bの本文

            ## 補足
            補足本文
            """;
        var document = new MarkdownKnowledgeChunkParser().Parse("example.md", markdown);

        Assert.Collection(
            document.Chunks,
            first =>
            {
                Assert.Equal("機能", first.ParentHeading);
                Assert.Equal("A", first.Heading);
            },
            second =>
            {
                Assert.Equal("機能", second.ParentHeading);
                Assert.Equal("B", second.Heading);
            },
            third =>
            {
                Assert.Null(third.ParentHeading);
                Assert.Equal("補足", third.Heading);
            });
    }
}
