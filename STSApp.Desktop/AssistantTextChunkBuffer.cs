using System.Collections.Generic;
using System.Text;

namespace STSApp.Desktop;

/// <summary>
/// 順序外や重複を含む部分テキスト通知を、先頭から連続した本文へまとめます。
/// </summary>
public sealed class AssistantTextChunkBuffer
{
    private readonly SortedDictionary<int, string> _chunks = [];
    private bool _isFinalized;

    public string? Add(int sequence, string text)
    {
        if (_isFinalized || sequence < 0 || !_chunks.TryAdd(sequence, text))
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var expected = 0; _chunks.TryGetValue(expected, out var chunk); expected++)
        {
            builder.Append(chunk);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    public void FinalizeText()
    {
        _isFinalized = true;
        _chunks.Clear();
    }
}
