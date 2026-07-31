using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using STSApp.Contracts.Models;

namespace STSApp.Desktop;

/// <summary>
/// 保存済みの会話を選ぶためのダイアログです。
/// </summary>
public sealed class ConversationHistoryDialog : Window
{
    private readonly ListBox _conversationList;
    private readonly Button _openButton;
    private readonly IReadOnlyList<ConversationDto> _conversations;

    public ConversationHistoryDialog(IReadOnlyList<ConversationDto> conversations)
    {
        _conversations = conversations;

        Title = "会話履歴";
        Width = 520;
        Height = 420;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _conversationList = new ListBox
        {
            ItemsSource = conversations
                .OrderByDescending(x => x.UpdatedAt)
                .Select(x => new ConversationListItem(x))
                .ToList(),
            Margin = new Thickness(0, 8, 0, 12)
        };
        _conversationList.SelectionChanged += ConversationList_SelectionChanged;

        _openButton = new Button
        {
            Content = "この会話を開く",
            IsEnabled = false,
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _openButton.Click += OpenButton_Click;

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(20)
        };

        content.Children.Add(new TextBlock
        {
            Text = conversations.Count == 0
                ? "保存された会話はありません。"
                : "表示する会話を選択してください。",
            FontSize = 15,
            Foreground = Avalonia.Media.Brushes.DimGray
        });

        Grid.SetRow(_conversationList, 1);
        content.Children.Add(_conversationList);

        var cancelButton = new Button
        {
            Content = "キャンセル",
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        cancelButton.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(_openButton);
        Grid.SetRow(buttons, 2);
        content.Children.Add(buttons);

        Content = content;
    }

    private void ConversationList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _openButton.IsEnabled = _conversationList.SelectedIndex >= 0;
    }

    private void OpenButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_conversationList.SelectedIndex < 0)
        {
            return;
        }

        var selected = _conversations
            .OrderByDescending(x => x.UpdatedAt)
            .ElementAt(_conversationList.SelectedIndex);
        Close(selected);
    }

    private sealed record ConversationListItem(ConversationDto Conversation)
    {
        public override string ToString()
        {
            return $"{Conversation.Title}  ({Conversation.UpdatedAt.ToLocalTime():yyyy/MM/dd HH:mm})";
        }
    }
}
