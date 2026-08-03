using System.Text;

namespace STSApp.Backend.Services;

/// <summary>
/// Geminiの任意の差分境界を、TTSへ渡せる自然な文境界へまとめます。
/// </summary>
public sealed class StreamingSentenceSegmenter
{
    private static readonly HashSet<char> SentenceTerminators = ['。', '！', '？', '!', '?'];
    private static readonly HashSet<char> ClosingCharacters = ['」', '』', '）', ')', '】', '］', '〕', '〉', '》', '〟', '”', '’'];
    private static readonly IReadOnlyDictionary<char, char> DelimiterPairs = new Dictionary<char, char>
    {
        ['「'] = '」',
        ['『'] = '』',
        ['（'] = '）',
        ['('] = ')',
        ['【'] = '】',
        ['［'] = '］',
        ['〔'] = '〕',
        ['〈'] = '〉',
        ['《'] = '》',
        ['〝'] = '〟',
        ['“'] = '”',
        ['‘'] = '’'
    };
    private readonly StringBuilder _buffer = new();

    public IReadOnlyList<string> Append(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _buffer.Append(text);
        }

        return Drain(final: false);
    }

    public IReadOnlyList<string> Complete()
    {
        return Drain(final: true);
    }

    private IReadOnlyList<string> Drain(bool final)
    {
        var sentences = new List<string>();

        while (_buffer.Length > 0)
        {
            var boundary = FindBoundary(final);
            if (boundary < 0)
            {
                break;
            }

            var sentence = _buffer.ToString(0, boundary).Trim();
            _buffer.Remove(0, boundary);
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }
        }

        if (final)
        {
            var remainder = _buffer.ToString().Trim();
            _buffer.Clear();
            if (remainder.Length > 0)
            {
                sentences.Add(remainder);
            }
        }

        return sentences;
    }

    private int FindBoundary(bool final)
    {
        for (var index = 0; index < _buffer.Length; index++)
        {
            if (_buffer[index] is '\r' or '\n')
            {
                var end = index + 1;
                if (_buffer[index] == '\r' && end < _buffer.Length && _buffer[end] == '\n')
                {
                    end++;
                }

                return end;
            }

            if (!SentenceTerminators.Contains(_buffer[index]))
            {
                continue;
            }

            var sentenceEnd = index + 1;
            while (sentenceEnd < _buffer.Length && ClosingCharacters.Contains(_buffer[sentenceEnd]))
            {
                sentenceEnd++;
            }

            // 未閉鎖の引用符・括弧内でSSE差分が文末記号の直後に切れた場合だけ、
            // 次の差分に閉じ記号が続く可能性があるため確定を待ちます。
            // 通常文は待たずに確定し、初回TTS開始の遅延を増やしません。
            if (!final
                && sentenceEnd == _buffer.Length
                && HasUnclosedDelimiter(sentenceEnd))
            {
                return -1;
            }

            return sentenceEnd;
        }

        return -1;
    }

    private bool HasUnclosedDelimiter(int length)
    {
        foreach (var (opening, closing) in DelimiterPairs)
        {
            var balance = 0;
            for (var index = 0; index < length; index++)
            {
                if (_buffer[index] == opening)
                {
                    balance++;
                }
                else if (_buffer[index] == closing && balance > 0)
                {
                    balance--;
                }
            }

            if (balance > 0)
            {
                return true;
            }
        }

        return false;
    }
}
