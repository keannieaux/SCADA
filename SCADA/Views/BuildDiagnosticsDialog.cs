using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using SCADA.Package.Builder;

namespace SCADA.Views;

internal static class BuildDiagnosticsDialog
{
    public static Task Show(Window owner, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var diagnostic in diagnostics)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = diagnostic.Severity.ToString(),
                Foreground = SeverityBrush(diagnostic.Severity),
                FontWeight = FontWeight.Bold,
                Width = 70
            });
            row.Children.Add(new TextBlock { Text = diagnostic.Source, Width = 140, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = diagnostic.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer { Content = list, MaxHeight = 400 };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "Диагностика сборки", FontWeight = FontWeight.Bold, FontSize = 16 });
        panel.Children.Add(scroll);

        Window? dialog = null;
        var okButton = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        okButton.Click += (_, _) => dialog!.Close();
        panel.Children.Add(okButton);

        dialog = new Window
        {
            Content = panel,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Title = "Диагностика сборки"
        };

        return dialog.ShowDialog(owner);
    }

    private static IBrush SeverityBrush(BuildSeverity severity)
    {
        string key = severity switch
        {
            BuildSeverity.Error => "CritBrush",
            BuildSeverity.Warning => "WarnBrush",
            _ => "TextBrush"
        };

        if (Application.Current is { } app && app.TryGetResource(key, ThemeVariant.Default, out var value) && value is IBrush brush)
            return brush;

        return Brushes.White;
    }
}
