using System.Security.Cryptography;
using System.Text;

namespace STSApp.Backend.Services.Rag;

/// <summary>
/// VoiceLink資料のMarkdownを、意味を保ったまま検索単位へ分割します。
/// Markdown全般を完全に解釈する用途ではなく、今回決めた見出しルールだけを意図的に扱います。
/// </summary>
public sealed class MarkdownKnowledgeChunkParser
{
    public KnowledgeSourceDocument Parse(string sourcePath, string markdown)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("資料パスが空です。", nameof(sourcePath));
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new InvalidOperationException($"資料 '{sourcePath}' が空です。");
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var title = FindFirstHeading(lines, "# ") ?? Path.GetFileNameWithoutExtension(sourcePath);
        var sections = ReadLevelTwoSections(lines);
        var chunks = new List<KnowledgeChunkDraft>();
        var order = 0;

        foreach (var section in sections)
        {
            // ### がある ## は、その子見出しごとに分けます。
            // 親の##自体を別チャンクにすると、同じ説明が複数チャンクへ重なって検索精度が下がるためです。
            var levelThreeSections = ReadLevelThreeSections(section.BodyLines);
            if (levelThreeSections.Count > 0)
            {
                foreach (var child in levelThreeSections)
                {
                    AddChunk(chunks, section.Heading, child.Heading, child.BodyLines, ref order);
                }

                continue;
            }

            AddChunk(chunks, null, section.Heading, section.BodyLines, ref order);
        }

        if (chunks.Count == 0)
        {
            throw new InvalidOperationException($"資料 '{sourcePath}' に ## または ### の検索対象見出しがありません。");
        }

        return new KnowledgeSourceDocument(
            sourcePath,
            title,
            ComputeHash(markdown),
            chunks);
    }

    private static void AddChunk(
        ICollection<KnowledgeChunkDraft> chunks,
        string? parentHeading,
        string heading,
        IReadOnlyList<string> bodyLines,
        ref int order)
    {
        var content = string.Join("\n", bodyLines).Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"見出し '{heading}' に本文がありません。");
        }

        order++;
        chunks.Add(new KnowledgeChunkDraft(
            parentHeading,
            heading,
            content,
            order,
            ComputeHash($"{parentHeading}\n{heading}\n{content}")));
    }

    private static List<MarkdownSection> ReadLevelTwoSections(IReadOnlyList<string> lines)
    {
        var sections = new List<MarkdownSection>();
        MarkdownSectionBuilder? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(current.Build());
                }

                current = new MarkdownSectionBuilder(line[3..].Trim());
                continue;
            }

            current?.BodyLines.Add(line);
        }

        if (current is not null)
        {
            sections.Add(current.Build());
        }

        return sections;
    }

    private static List<MarkdownSection> ReadLevelThreeSections(IReadOnlyList<string> lines)
    {
        var sections = new List<MarkdownSection>();
        MarkdownSectionBuilder? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(current.Build());
                }

                current = new MarkdownSectionBuilder(line[4..].Trim());
                continue;
            }

            current?.BodyLines.Add(line);
        }

        if (current is not null)
        {
            sections.Add(current.Build());
        }

        return sections;
    }

    private static string? FindFirstHeading(IEnumerable<string> lines, string prefix)
    {
        return lines
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?
            .Substring(prefix.Length)
            .Trim();
    }

    public static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record MarkdownSection(string Heading, IReadOnlyList<string> BodyLines);

    private sealed class MarkdownSectionBuilder
    {
        public MarkdownSectionBuilder(string heading)
        {
            Heading = heading;
        }

        public string Heading { get; }
        public List<string> BodyLines { get; } = [];

        public MarkdownSection Build() => new(Heading, BodyLines);
    }
}
