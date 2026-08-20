using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        ("â‡§ â†  â†’", "Shift+Left/Right"),
        ("â†  â†’", "Left/Right"),
        ("ðŸŽ¬ ", "VIDEO "),
        ("ðŸ“Œ", "PIN"),
        ("ðŸ“ ", "PIN"),
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
        if (root is ComboBox or ComboBoxItem or DataGrid or ListView)
            return;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox or ComboBoxItem or DataGrid or ListView)
                continue;

            SanitizeNode(child);
            TraverseVisual(child);
        }
    }

    private static void SanitizeNode(object node)
    {
        switch (node)
        {
            case TextBlock textBlock:
                if (textBlock.Inlines.Count > 0)
                {
                    foreach (var inline in textBlock.Inlines)
                    {
                        if (inline is Run inlineRun)
                        {
                            inlineRun.Text = Normalize(inlineRun.Text, trim: false);
                        }
                    }
                }
                else if (!BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty) && !string.IsNullOrEmpty(textBlock.Text))
                {
                    textBlock.Text = Normalize(textBlock.Text, trim: false);
                }
                break;
            case Run run:
                run.Text = Normalize(run.Text, trim: false);
                break;
            case ContentControl contentControl when contentControl is not ComboBoxItem:
                if (contentControl.Content is string text && !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty))
                {
                    contentControl.Content = Normalize(text);
                }
                break;
            case HeaderedContentControl headeredContentControl:
                if (headeredContentControl.Header is string headerText && !BindingOperations.IsDataBound(headeredContentControl, HeaderedContentControl.HeaderProperty))
                {
                    headeredContentControl.Header = Normalize(headerText);
                }
                break;
            case TextBox textBox:
                if (textBox.Text.Contains("â") && !BindingOperations.IsDataBound(textBox, TextBox.TextProperty))
                {
                    textBox.Text = Normalize(textBox.Text);
                }
                break;
        }
    }

    public static string Normalize(string? input, bool trim = true)
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

        return trim ? output.Trim() : output;
    }
}
