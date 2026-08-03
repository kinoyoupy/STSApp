using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Diagnostics;

namespace STSApp.Desktop;

/// <summary>
/// VAD導入に向けて、待機中の短い音声フレームも受け取れる録音処理です。
///
/// 待機中は20ms単位のPCM音声をFrameCapturedイベントで通知するだけにし、
/// VADが発話開始を決めた後でだけ、完成WAVの保存を始めます。
/// </summary>
public sealed class ContinuousAudioRecorder : IDisposable
{
    private const int SampleRate = 16_000;
    private const int VadFrameSampleCount = 320;
    private const short BitsPerSample = 16;
    private const short ChannelCount = 1;

    private readonly NativeMethods.AudioFrameCallback _frameCallback;
    private readonly GCHandle _selfHandle;
    private IntPtr _nativeRecorder;
    private bool _isListening;
    private bool _isCapturingAudio;
    private int _capturedFrameCount;
    private string? _callbackErrorMessage;
    private volatile bool _isDisposed;

    public ContinuousAudioRecorder()
    {
        // ネイティブ側からC#のcallbackを呼び戻す間、このインスタンスをGCが回収しないように保持します。
        _selfHandle = GCHandle.Alloc(this);
        _frameCallback = OnNativeFrameCaptured;
        _nativeRecorder = NativeMethods.Create(
            SampleRate,
            _frameCallback,
            GCHandle.ToIntPtr(_selfHandle));

        if (_nativeRecorder == IntPtr.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException("macOSの連続録音サービスを初期化できませんでした。");
        }
    }

    /// <summary>
    /// 16kHz・モノラル・16bit PCMの20ms音声フレームを通知します。
    ///
    /// このイベントは音声処理用のスレッドから呼ばれます。
    /// 購読側は画面更新やネットワーク通信をせず、短時間で処理を終える必要があります。
    /// </summary>
    public event Action<ReadOnlyMemory<short>>? FrameCaptured;

    /// <summary>
    /// 今回の待機・録音中にC#側へ届いた20msフレーム数です。
    /// マイク入力が連続して届いているかを確認するために保持します。
    /// </summary>
    public int CapturedFrameCount => Volatile.Read(ref _capturedFrameCount);

    public void StartListening()
    {
        if (_isListening)
        {
            throw new InvalidOperationException("Voice input is already listening.");
        }

        Interlocked.Exchange(ref _capturedFrameCount, 0);
        Interlocked.Exchange(ref _callbackErrorMessage, null);

        var errorBuffer = new byte[1024];
        if (!NativeMethods.CheckMicrophonePermission(errorBuffer))
        {
            var permissionMessage = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(permissionMessage)
                    ? "macOSのマイク権限を確認できませんでした。"
                    : permissionMessage);
        }

        if (!NativeMethods.Start(_nativeRecorder, errorBuffer))
        {
            var message = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "macOSの連続マイク入力を開始できませんでした。"
                    : message);
        }

        _isListening = true;
        _isCapturingAudio = false;
    }

    /// <summary>
    /// VADが発話開始を検知した時点で呼び、WAVへの保存を開始します。
    /// 待機中の無音を保存せず、ネイティブ側が保持した直前400msだけを先頭に含めます。
    /// </summary>
    public void BeginAudioCapture()
    {
        if (!_isListening)
        {
            throw new InvalidOperationException("Voice input has not started listening.");
        }

        if (_isCapturingAudio)
        {
            return;
        }

        var errorBuffer = new byte[1024];
        if (!NativeMethods.BeginAudioCapture(_nativeRecorder, errorBuffer))
        {
            var message = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "発話音声の保存を開始できませんでした。"
                    : message);
        }

        _isCapturingAudio = true;
    }

    /// <summary>
    /// 発話が終わった時に監視を止め、保存済みWAVを返します。
    /// </summary>
    public RecordedAudio StopAndGetAudio()
    {
        if (!_isListening)
        {
            throw new InvalidOperationException("Voice input has not started listening.");
        }

        if (!_isCapturingAudio)
        {
            StopNative();
            throw new InvalidOperationException("発話開始前のため、送信する録音データがありません。");
        }

        StopNative();

        // 待機中も含むフレーム数が一つもない場合、マイク入力自体を取得できていません。
        if (CapturedFrameCount == 0)
        {
            throw new InvalidOperationException("録音中の音声フレームを取得できませんでした。");
        }

        var wavBytes = NativeMethods.CopyWav(_nativeRecorder);
        if (wavBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "録音データが空でした。マイク入力またはmacOSのマイク権限を確認してください。");
        }

        return new RecordedAudio(
            wavBytes,
            $"continuous-recording-{DateTime.UtcNow:yyyyMMddHHmmss}.wav",
            "audio/wav");
    }

    /// <summary>
    /// ユーザーが待機を明示的に止めた時など、保存済み音声を送らずに破棄します。
    /// </summary>
    public void StopAndDiscard()
    {
        if (_isListening)
        {
            StopNative();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_isListening)
        {
            try
            {
                StopNative();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"ContinuousAudioRecorder stop during dispose failed: {exception.GetType().Name}");
            }
        }

        _isListening = false;
        _isCapturingAudio = false;

        if (_nativeRecorder != IntPtr.Zero)
        {
            NativeMethods.Destroy(_nativeRecorder);
            _nativeRecorder = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private static void OnNativeFrameCaptured(IntPtr samples, int sampleCount, IntPtr context)
    {
        if (samples == IntPtr.Zero || sampleCount != VadFrameSampleCount || context == IntPtr.Zero)
        {
            return;
        }

        ContinuousAudioRecorder? recorder = null;
        try
        {
            var selfHandle = GCHandle.FromIntPtr(context);
            if (selfHandle.Target is not ContinuousAudioRecorder resolvedRecorder
                || resolvedRecorder._isDisposed)
            {
                return;
            }

            recorder = resolvedRecorder;
            recorder.HandleFrameCaptured(samples, sampleCount);
        }
        catch (Exception exception)
        {
            // reverse P/Invokeコールバックから例外を外へ出すと.NETランタイムが
            // プロセスをSIGABRTで停止するため、録音停止時にUIへ返せる形で保持します。
            recorder?.RememberCallbackError(exception);
        }
    }

    private void HandleFrameCaptured(IntPtr samples, int sampleCount)
    {
        Interlocked.Increment(ref _capturedFrameCount);

        var frameCaptured = FrameCaptured;
        if (frameCaptured is null)
        {
            return;
        }

        // ネイティブ側のバッファはcallback終了後に再利用されます。
        // C#側へ通知する場合だけコピーし、次のWebRTC VAD処理が安全に読めるようにします。
        var managedSamples = new short[sampleCount];
        Marshal.Copy(samples, managedSamples, 0, sampleCount);
        frameCaptured(managedSamples);
    }

    private void StopNative()
    {
        _isListening = false;
        _isCapturingAudio = false;

        var stopped = NativeMethods.Stop(_nativeRecorder);
        var callbackError = Interlocked.Exchange(ref _callbackErrorMessage, null);
        if (!stopped || callbackError is not null)
        {
            throw new InvalidOperationException(callbackError
                ?? NativeMethods.GetLastError(_nativeRecorder)
                ?? "macOSの連続録音を停止できませんでした。");
        }
    }

    private void RememberCallbackError(Exception exception)
    {
        Interlocked.CompareExchange(
            ref _callbackErrorMessage,
            $"音声フレーム処理に失敗しました ({exception.GetType().Name})。",
            null);
    }

    private static class NativeMethods
    {
        private const string LibraryName = "libsts_audio_recorder.dylib";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void AudioFrameCallback(IntPtr samples, int sampleCount, IntPtr context);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_create", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeCreate(
            int sampleRate,
            AudioFrameCallback frameCallback,
            IntPtr callbackContext);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_start", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeStart(IntPtr recorder, byte[] errorMessage, int errorCapacity);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_check_microphone_permission", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeCheckMicrophonePermission(byte[] errorMessage, int errorCapacity);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_begin_audio_capture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeBeginAudioCapture(IntPtr recorder, byte[] errorMessage, int errorCapacity);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_stop", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeStop(IntPtr recorder);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_copy_wav", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeCopyWav(IntPtr recorder, out IntPtr data, out int size);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeGetLastError(IntPtr recorder, byte[] errorMessage, int errorCapacity);

        [DllImport(LibraryName, EntryPoint = "sts_continuous_audio_recorder_destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void NativeDestroy(IntPtr recorder);

        [DllImport("libSystem.B.dylib")]
        private static extern void free(IntPtr pointer);

        public static IntPtr Create(int sampleRate, AudioFrameCallback frameCallback, IntPtr callbackContext)
            => NativeCreate(sampleRate, frameCallback, callbackContext);

        public static bool Start(IntPtr recorder, byte[] errorMessage)
            => NativeStart(recorder, errorMessage, errorMessage.Length) != 0;

        public static bool CheckMicrophonePermission(byte[] errorMessage)
            => NativeCheckMicrophonePermission(errorMessage, errorMessage.Length) != 0;

        public static bool BeginAudioCapture(IntPtr recorder, byte[] errorMessage)
            => NativeBeginAudioCapture(recorder, errorMessage, errorMessage.Length) != 0;

        public static bool Stop(IntPtr recorder)
            => NativeStop(recorder) != 0;

        public static string? GetLastError(IntPtr recorder)
        {
            var errorBuffer = new byte[1024];
            if (NativeGetLastError(recorder, errorBuffer, errorBuffer.Length) == 0)
            {
                return null;
            }

            var message = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }

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
                free(data);
            }
        }

        public static void Destroy(IntPtr recorder) => NativeDestroy(recorder);
    }
}
