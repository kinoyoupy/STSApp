using STSApp.Desktop;
using Xunit;

namespace STSApp.Desktop.Tests;

public sealed class AssistantTextChunkBufferTests
{
    [Fact]
    public void Appends_only_the_contiguous_prefix_when_notifications_arrive_out_of_order()
    {
        var buffer = new AssistantTextChunkBuffer();

        Assert.Null(buffer.Add(1, "二文目。"));
        Assert.Equal("一文目。二文目。", buffer.Add(0, "一文目。"));
        Assert.Equal("一文目。二文目。三文目。", buffer.Add(2, "三文目。"));
    }

    [Fact]
    public void Ignores_duplicate_and_invalid_notifications()
    {
        var buffer = new AssistantTextChunkBuffer();

        Assert.Equal("一文目。", buffer.Add(0, "一文目。"));
        Assert.Null(buffer.Add(0, "重複。"));
        Assert.Null(buffer.Add(-1, "不正。"));
    }

    [Fact]
    public void Ignores_late_chunks_after_the_full_text_is_finalized()
    {
        var buffer = new AssistantTextChunkBuffer();
        Assert.Equal("一文目。", buffer.Add(0, "一文目。"));

        buffer.FinalizeText();

        Assert.Null(buffer.Add(1, "遅延通知。"));
    }
}
