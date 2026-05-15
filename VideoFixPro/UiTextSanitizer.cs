using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace VideoFixPro;

internal static class UiTextSanitizer
{
    private static readonly (string From, string To)[] Replacements =
    {
        ("â–¶  Fix All Videos", "Start Fix All Videos"),
        ("âœ‚  Trim Video", "Trim Video"),
        ("âœ‚  Open Trim Tool", "Open Trim Tool"),
        ("â—¼  Cancel", "Cancel"),
        ("ðŸ“‚  Open Folder", "Open Folder"),
        ("ðŸ—‘ Remove", "Remove"),
        ("â†© Reset to source folder", "<- Reset to source folder"),
        ("â†© Full", "Reset Full"),
        ("â‡§ â† â†’", "Shift+Left/Right"),
        ("â† â†’", "Left/Right"),
        ("ðŸŽ¬ ", "VIDEO "),
        ("ðŸ“Œ", "PIN"),
        ("ðŸ“", "PIN"),
        ("ðŸ“‚", "DIR"),
        ("âœ‚", "TRIM"),
        ("âœ•", "X"),
        ("â”€", "_"),
        ("â–¡", "[ ]"),
        ("â–²", "^"),
        ("â–¼", "v"),
        ("â–¶", ">"),
        ("â€–", "||"),
        ("â—¼", ""),
        ("â—€", "O"),
        ("â—·", "."),
        ("âœ”", "OK"),
        ("âœ–", "X"),
        ("â€¢", "-"),
        ("Â·", "-"),
        ("â†’", "->"),
        ("â€”", "-"),
        ("Ã—", "x"),
        ("â€¦", "..."),
        ("â€“", "-"),
        ("â€”", "-"),
        ("Â±", "+/-")
    };

    public static void Apply(DependencyObject root)
    {
        SanitizeNode(root);
        TraverseVisual(root);
    }

    private static void TraverseVisual(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            SanitizeNode(child);
            TraverseVisual(child);
        }
    }

    private static void SanitizeNode(object node)
    {
        switch (node)
        {
            case TextBlock textBlock:
                textBlock.Text = Normalize(textBlock.Text);
                break;
            case Run run:
                run.Text = Normalize(run.Text);
                break;
            case ContentControl contentControl when contentControl.Content is string text:
                contentControl.Content = Normalize(text);
                break;
            case HeaderedContentControl headeredContentControl when headeredContentControl.Header is string text:
                headeredContentControl.Header = Normalize(text);
                break;
            case TextBox textBox:
                if (textBox.Text.Contains("â"))
                {
                    textBox.Text = Normalize(textBox.Text);
                }
                break;
        }
    }

    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var output = input;
        foreach (var (from, to) in Replacements)
        {
            output = output.Replace(from, to, StringComparison.Ordinal);
        }

        while (output.Contains("  ", StringComparison.Ordinal))
        {
            output = output.Replace("  ", " ", StringComparison.Ordinal);
        }

        return output.Trim();
    }
}
