using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace STSApp.Desktop;

/// <summary>
/// アプリ終了時に、音声ファイルの扱いを利用者へ伝える確認画面です。
/// 音声は削除されますが、文字による会話履歴は残ることを明示します。
/// </summary>
public sealed class CloseConfirmationDialog : Window
{
    public CloseConfirmationDialog()
    {
        Title = "終了確認";
        Width = 430;
        Height = 210;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var title = new TextBlock
        {
            Text = "アプリを終了しますか？",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#20242C"))
        };

        var message = new TextBlock
        {
            Text = "終了すると、この会話の音声ファイルは削除されます。",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#657085"))
        };

        var cancelButton = new Button
        {
            Content = "キャンセル",
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        cancelButton.Click += (_, _) => Close(false);

        var closeButton = new Button
        {
            Content = "終了する",
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeButton.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, closeButton }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children = { title, message, buttons }
        };
    }
}
