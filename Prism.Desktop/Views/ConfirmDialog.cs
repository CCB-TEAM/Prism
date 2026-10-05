using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prism.Desktop.Views;

/// <summary>极简确认对话框（Avalonia 无内置 MessageBox）。</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string message, string title = "确认")
    {
        var ok = new Button { Content = "确定", MinWidth = 80 };
        var cancel = new Button { Content = "取消", MinWidth = 80 };

        var panel = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(16),
        };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = panel,
        };

        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }
}
