namespace STSApp.Backend.Services.External;

/// <summary>
/// 実TTS APIを設定する前に、返答音声の保存・取得・再生まで確認するための開発用TTSです。
/// 返答テキストから本物の音声合成はせず、短い無音WAVを作ります。
/// </summary>
public sealed class DevelopmentMockTtsClient : ITtsClient
{
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;
    private const int DurationMilliseconds = 700;

    public Task<GeneratedSpeech> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken)
    {
        // TTSの後続処理では「WAVバイナリが返ること」が重要です。
        // そのため、開発用では再生可能な短い無音WAVをメモリ上で作って返します。
        var audioBytes = CreateSilentWav();
        Stream audioStream = new MemoryStream(audioBytes);
        return Task.FromResult(new GeneratedSpeech(audioStream, "audio/wav", ".wav"));
    }

    private static byte[] CreateSilentWav()
    {
        var bytesPerSample = BitsPerSample / 8;
        var sampleCount = SampleRate * DurationMilliseconds / 1000;
        var dataSize = sampleCount * Channels * bytesPerSample;
        var totalSize = 44 + dataSize;
        var buffer = new byte[totalSize];

        WriteAscii(buffer, 0, "RIFF");
        WriteInt32LittleEndian(buffer, 4, totalSize - 8);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        WriteInt32LittleEndian(buffer, 16, 16);
        WriteInt16LittleEndian(buffer, 20, 1);
        WriteInt16LittleEndian(buffer, 22, Channels);
        WriteInt32LittleEndian(buffer, 24, SampleRate);
        WriteInt32LittleEndian(buffer, 28, SampleRate * Channels * bytesPerSample);
        WriteInt16LittleEndian(buffer, 32, (short)(Channels * bytesPerSample));
        WriteInt16LittleEndian(buffer, 34, BitsPerSample);
        WriteAscii(buffer, 36, "data");
        WriteInt32LittleEndian(buffer, 40, dataSize);

        // 44バイト目以降の音声データ部分は0のままにします。
        // 16bit PCMでは0が無音なので、これだけで再生可能な無音WAVになります。
        return buffer;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            buffer[offset + i] = (byte)value[i];
        }
    }

    private static void WriteInt16LittleEndian(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        buffer[offset + 2] = (byte)((value >> 16) & 0xff);
        buffer[offset + 3] = (byte)((value >> 24) & 0xff);
    }
}
