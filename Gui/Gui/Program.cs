using System;
using System.Threading;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Gui;

/// <summary>
/// Entry point. A toast click on an unpackaged app starts a second process through COM, and
/// the click's arguments reach it as an activation event rather than on the command line, so
/// they have to be collected here before XAML starts.
/// </summary>
public static class Program
{
    /// <summary>The clicked notification's launch argument, e.g. "session=&lt;uuid&gt;".</summary>
    internal static string? NotificationArgument;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (Array.Exists(args, arg => arg.StartsWith(App.NotificationActivationPrefix, StringComparison.Ordinal)))
            ReadNotificationArgument();

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    private static void ReadNotificationArgument()
    {
        try
        {
            AppNotificationManager manager = AppNotificationManager.Default;
            using var arrived = new ManualResetEventSlim(false);
            manager.NotificationInvoked += (_, invoked) =>
            {
                NotificationArgument = invoked.Argument;
                arrived.Set();
            };

            // Registering is what makes this process the notification activator; the SDK
            // fails fast if the activation arguments are read before it.
            manager.Register();

            AppActivationArguments activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == ExtendedActivationKind.AppNotification)
                NotificationArgument = ((AppNotificationActivatedEventArgs)activated.Data).Argument;
            else
                arrived.Wait(2000);

            Diag.Log("launch", $"notification kind={activated.Kind} argument={NotificationArgument ?? "-"}");
        }
        catch (Exception error)
        {
            Diag.LogException("Program.notification", error);
        }
    }
}