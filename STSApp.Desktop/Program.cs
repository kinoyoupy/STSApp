using Avalonia;
using System;

namespace STSApp.Desktop;

class Program
{
    // デスクトップアプリの開始地点です。
    // Avaloniaの画面やマイクなど、UIに依存する処理はAvaloniaの初期化後に実行します。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avaloniaの基本設定をまとめます。
    // UsePlatformDetectにより、実行しているOSに合わせた画面機能が選ばれます。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
