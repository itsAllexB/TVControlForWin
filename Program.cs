// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace TVControlForWin;

/// <summary>
/// Custom entry point (DisableXamlGeneratedMain=true) so we can enforce a single
/// instance: a second launch redirects activation to the already-running tray app
/// instead of opening a second window.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return; // another instance is already running; we redirected to it

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool DecideRedirection()
    {
        // An instance pointed at its own data folder is a separate app as far as this goes.
        var key = "TVControlForWin-single-instance";
        if (Services.AppPaths.OverrideDir is { } dataDir)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant()));
            key += "-" + Convert.ToHexString(hash, 0, 4);
        }
        var keyInstance = AppInstance.FindOrRegisterForKey(key);

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, _) => (App.Current as App)?.OnRedirected();
            return false;
        }

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        keyInstance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
        return true;
    }
}
