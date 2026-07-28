using System;
using System.Runtime.InteropServices;
using System.Text;

namespace STSApp.Desktop;

/// <summary>
/// PushToTalkの録音処理を担当するクラスです。
///
/// 録音の実体は、macOS標準のAVAudioRecorderを使うネイティブブリッジへ委譲します。
/// このクラスは、録音の開始・停止と、Backendへ渡す録音結果の整形を担当します。
/// </summary>
public sealed class PushToTalkAudioRecorder : IDisposable
{
    private const int SampleRate = 16_000;
    private const short BitsPerSample = 16;
    private const short ChannelCount = 1;

    private IntPtr _nativeRecorder;
    private DateTime? _startedAt;

    public PushToTalkAudioRecorder()
    {
        _nativeRecorder = NativeMethods.Create(SampleRate);
        if (_nativeRecorder == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOSの録音サービスを初期化できませんでした。");
        }
    }

    public void Start()
    {
        // C#側でも録音状態を確認する理由は、同じボタン操作で録音を二重開始しないためです。
        // ネイティブ側にも状態はありますが、画面に近いC#側で先に防ぎます。
        if (_startedAt is not null)
        {
            throw new InvalidOperationException("Recording is already running.");
        }

        var errorBuffer = new byte[1024];
        if (!NativeMethods.Start(_nativeRecorder, errorBuffer))
        {
            var message = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "macOSのマイク入力を開始できませんでした。"
                    : message);
        }

        _startedAt = DateTime.UtcNow;
    }

    public RecordedAudio Stop()
    {
        // WAVをbyte[]へコピーする理由は、ネイティブ側の一時ファイルを閉じた後でもBackendへ送れるようにするためです。
        // コピー後のRecordedAudioが、Backendへ送るデータになります。
        if (_startedAt is null)
        {
            throw new InvalidOperationException("Recording has not started.");
        }

        _startedAt = null;

        if (!NativeMethods.Stop(_nativeRecorder))
        {
            throw new InvalidOperationException("macOSの録音を停止できませんでした。");
        }

        var wavBytes = NativeMethods.CopyWav(_nativeRecorder);
        if (wavBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "録音データが空でした。マイク入力またはmacOSのマイク権限を確認してください。");
        }

        return new RecordedAudio(
            wavBytes,
            $"push-to-talk-{DateTime.UtcNow:yyyyMMddHHmmss}.wav",
            "audio/wav");
    }

    public void Dispose()
    {
        _startedAt = null;

        if (_nativeRecorder != IntPtr.Zero)
        {
            NativeMethods.Destroy(_nativeRecorder);
            _nativeRecorder = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        private const string LibraryName = "libsts_audio_recorder.dylib";

        [DllImport(LibraryName, EntryPoint = "sts_audio_recorder_create")]
        private static extern IntPtr NativeCreate(int sampleRate);

        [DllImport(LibraryName, EntryPoint = "sts_audio_recorder_start")]
        private static extern int NativeStart(IntPtr recorder, byte[] errorMessage, int errorCapacity);

        [DllImport(LibraryName, EntryPoint = "sts_audio_recorder_stop")]
        private static extern int NativeStop(IntPtr recorder);

        [DllImport(LibraryName, EntryPoint = "sts_audio_recorder_copy_wav")]
        private static extern int NativeCopyWav(
            IntPtr recorder,
            out IntPtr data,
            out int size);

        [DllImport(LibraryName, EntryPoint = "sts_audio_recorder_destroy")]
        private static extern void NativeDestroy(IntPtr recorder);

        public static IntPtr Create(int sampleRate) => NativeCreate(sampleRate);

        public static bool Start(IntPtr recorder, byte[] errorMessage)
            => NativeStart(recorder, errorMessage, errorMessage.Length) != 0;

        public static bool Stop(IntPtr recorder)
            => NativeStop(recorder) != 0;

        public static byte[] CopyWav(IntPtr recorder)
        {
            if (NativeCopyWav(recorder, out var data, out var size) == 0 || data == IntPtr.Zero || size <= 0)
            {
                return Array.Empty<byte>();
            }

            try
            {
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, size);
                return bytes;
            }
            finally
            {
                NativeFree(data);
            }
        }

        public static void Destroy(IntPtr recorder) => NativeDestroy(recorder);

        [DllImport("libSystem.B.dylib")]
        private static extern void free(IntPtr pointer);

        private static void NativeFree(IntPtr pointer) => free(pointer);
    }
}
