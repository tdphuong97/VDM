using Photino.NET;
using VDM.Core.Host;

namespace VDM.Mac;

/// <summary>The small window that pops up when the browser hands VDM a link.</summary>
static class CaptureDialog
{
    const int Width = 560;

    public static void Open(MacPlatform platform, PhotinoWindow main, CaptureRequest request)
    {
        PhotinoWindow? window = null;
        CaptureSession? session = null;
        // The window's frame around the page: the title bar.
        var chrome = OperatingSystem.IsMacOS() ? 28 : 39;

        window = new PhotinoWindow(main) { LogVerbosity = 0 }
            .SetTitle(request.IsMedia ? "VDM - Tải video" : "VDM - Tải file")
            .SetUseOsDefaultSize(false)
            .SetSize(Width, request.IsMedia ? 600 : 400)
            .SetResizable(false)
            .SetTopMost(true)
            .SetContextMenuEnabled(false)
            .Center();
        session = new CaptureSession(platform.Host, request, window,
            close: () => window.Invoke(window.Close),
            fitHeight: content =>
            {
                if (content <= 0) return;
                window.Invoke(() =>
                {
                    var target = Math.Clamp(content + chrome, 240, 900);
                    if (Math.Abs(window.Height - target) > 2) window.SetSize(Width, target).Center();
                });
            });
        session.Begin();
        _ = new PhotinoHost(window, session.HandleAsync);
        window.RegisterWindowClosingHandler((_, _) =>
        {
            session.Cancel();
            return false;
        });
        window.Load(new Uri(PhotinoHost.Url("capture.html")));
        window.WaitForClose(); // a child window: shows it and returns
    }
}
