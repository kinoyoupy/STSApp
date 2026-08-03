using STSApp.Backend.Services;

namespace STSApp.Backend.Tests;

public sealed class StreamingSentenceSegmenterTests
{
    [Fact]
    public void Emits_japanese_sentences_across_arbitrary_deltas()
    {
        var segmenter = new StreamingSentenceSegmenter();

        Assert.Empty(segmenter.Append("最初の"));
        Assert.Equal(["最初の文です。"], segmenter.Append("文です。"));
        Assert.Equal(["次です！"], segmenter.Append("次です！続き"));
        Assert.Equal(["続き"], segmenter.Complete());
    }

    [Fact]
    public void Keeps_closing_characters_and_splits_newlines()
    {
        var segmenter = new StreamingSentenceSegmenter();

        Assert.Equal(["「大丈夫？」", "次"], segmenter.Append("「大丈夫？」次\n残り"));
        Assert.Empty(segmenter.Append(string.Empty));
        Assert.Equal(["残り"], segmenter.Complete());
    }

    [Fact]
    public void Ignores_empty_remainder()
    {
        var segmenter = new StreamingSentenceSegmenter();

        Assert.Equal(["完了。"], segmenter.Append("完了。"));
        Assert.Empty(segmenter.Complete());
    }

    [Fact]
    public void Keeps_closing_characters_that_arrive_in_the_next_delta()
    {
        var segmenter = new StreamingSentenceSegmenter();

        Assert.Empty(segmenter.Append("「大丈夫？"));
        Assert.Equal(["「大丈夫？」"], segmenter.Append("」次の文"));
        Assert.Equal(["次の文"], segmenter.Complete());
    }

    [Theory]
    [InlineData("句点。")]
    [InlineData("全角疑問符？")]
    [InlineData("全角感嘆符！")]
    [InlineData("半角疑問符?")]
    [InlineData("半角感嘆符!")]
    public void Emits_each_sentence_terminator_at_stream_completion(string text)
    {
        var segmenter = new StreamingSentenceSegmenter();

        Assert.Equal([text], segmenter.Append(text));
        Assert.Empty(segmenter.Complete());
    }
}
