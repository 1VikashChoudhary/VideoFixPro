using System.Diagnostics;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

internal static class DesktopNotification
{
    public static void Show(string title, string text, bool warning = false)
    {
        try
        {
            var ni = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty),
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = text,
                BalloonTipIcon = warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info
            };
            ni.ShowBalloonTip(3000);
            _ = Task.Delay(5000).ContinueWith(_ => ni.Dispose());
        }
        catch
        {
        }
    }
}
