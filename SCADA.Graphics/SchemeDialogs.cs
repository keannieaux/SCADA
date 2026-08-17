using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SCADA.Graphics;

internal static class SchemeDialogs
{
    public static Task ShowMessage(Window owner, string message)
    {
        Window? dialog = null;
        dialog = BuildDialog(message, ("OK", () => dialog!.Close()));
        return dialog.ShowDialog(owner);
    }

    public static Task<bool> ShowConfirm(Window owner, string message)
    {
        Window? dialog = null;
        dialog = BuildDialog(message,
            ("Да", () => dialog!.Close(true)),
            ("Нет", () => dialog!.Close(false)));
        return dialog.ShowDialog<bool>(owner);
    }

    private static Window BuildDialog(string message, params (string Label, Action OnClick)[] buttons)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 });

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, onClick) in buttons)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => onClick();
            buttonPanel.Children.Add(button);
        }
        panel.Children.Add(buttonPanel);

        return new Window
        {
            Content = panel,
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
    }
}
