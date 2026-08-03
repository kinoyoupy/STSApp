using System;
using System.Collections.Generic;

namespace STSApp.Desktop;

/// <summary>
/// SignalRの順序外通知とREST完了応答を統合し、音声を文番号順に一度だけ取り出します。
/// 値は追加時に作るため、後続音声のダウンロードを現在音声の再生中に先行できます。
/// </summary>
public sealed class OrderedAudioChunkBuffer<TValue>
{
    private readonly SortedDictionary<int, BufferedAudioChunk<TValue>> _chunks = [];
    private readonly HashSet<Guid> _knownAudioIds = [];

    public int NextSequence { get; private set; }
    public int? ExpectedChunkCount { get; private set; }
    public bool IsCancelled { get; private set; }
    public bool HasNext => _chunks.ContainsKey(NextSequence);
    public bool IsComplete => !IsCancelled
        && ExpectedChunkCount is int expected
        && NextSequence >= expected;

    public bool Add(int sequence, Guid audioId, Func<Guid, TValue> valueFactory)
    {
        if (IsCancelled
            || sequence < 0
            || sequence < NextSequence
            || _chunks.ContainsKey(sequence)
            || !_knownAudioIds.Add(audioId))
        {
            return false;
        }

        _chunks.Add(sequence, new BufferedAudioChunk<TValue>(audioId, valueFactory(audioId)));
        return true;
    }

    public void Restore(IReadOnlyList<Guid> audioIds, Func<Guid, TValue> valueFactory)
    {
        if (IsCancelled)
        {
            return;
        }

        ExpectedChunkCount = audioIds.Count;
        for (var sequence = 0; sequence < audioIds.Count; sequence++)
        {
            Add(sequence, audioIds[sequence], valueFactory);
        }
    }

    public bool TryTakeNext(out BufferedAudioChunk<TValue> chunk)
    {
        if (!_chunks.Remove(NextSequence, out var foundChunk))
        {
            chunk = null!;
            return false;
        }

        chunk = foundChunk;
        NextSequence++;
        return true;
    }

    public void Cancel()
    {
        IsCancelled = true;
        _chunks.Clear();
        ExpectedChunkCount = NextSequence;
    }
}

public sealed record BufferedAudioChunk<TValue>(Guid AudioId, TValue Value);
