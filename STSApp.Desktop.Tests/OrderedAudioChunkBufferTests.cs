using STSApp.Desktop;
using Xunit;

namespace STSApp.Desktop.Tests;

public sealed class OrderedAudioChunkBufferTests
{
    [Fact]
    public void Starts_values_immediately_but_takes_them_in_sequence_order()
    {
        var buffer = new OrderedAudioChunkBuffer<string>();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var started = new List<Guid>();

        Assert.True(buffer.Add(1, secondId, Start));
        Assert.False(buffer.HasNext);
        Assert.True(buffer.Add(0, firstId, Start));
        Assert.Equal([secondId, firstId], started);

        Assert.True(buffer.TryTakeNext(out var first));
        Assert.Equal(firstId, first!.AudioId);
        Assert.True(buffer.TryTakeNext(out var second));
        Assert.Equal(secondId, second!.AudioId);

        string Start(Guid id)
        {
            started.Add(id);
            return id.ToString();
        }
    }

    [Fact]
    public void Ignores_duplicate_sequence_and_audio_ids()
    {
        var buffer = new OrderedAudioChunkBuffer<Guid>();
        var audioId = Guid.NewGuid();

        Assert.True(buffer.Add(0, audioId, id => id));
        Assert.False(buffer.Add(0, Guid.NewGuid(), id => id));
        Assert.False(buffer.Add(1, audioId, id => id));
        Assert.True(buffer.TryTakeNext(out _));
        Assert.False(buffer.Add(0, Guid.NewGuid(), id => id));
    }

    [Fact]
    public void Restores_missing_notifications_from_the_rest_completion_list()
    {
        var buffer = new OrderedAudioChunkBuffer<Guid>();
        var audioIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        Assert.True(buffer.Add(1, audioIds[1], id => id));
        buffer.Restore(audioIds, id => id);

        var taken = new List<Guid>();
        while (buffer.TryTakeNext(out var chunk))
        {
            taken.Add(chunk!.AudioId);
        }

        Assert.Equal(audioIds, taken);
        Assert.True(buffer.IsComplete);
    }

    [Fact]
    public void Cancellation_stops_pending_and_future_audio()
    {
        var buffer = new OrderedAudioChunkBuffer<Guid>();
        buffer.Add(0, Guid.NewGuid(), id => id);

        buffer.Cancel();

        Assert.True(buffer.IsCancelled);
        Assert.False(buffer.HasNext);
        Assert.False(buffer.TryTakeNext(out _));
        Assert.False(buffer.Add(1, Guid.NewGuid(), id => id));
    }
}
