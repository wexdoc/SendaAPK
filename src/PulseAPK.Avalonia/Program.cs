// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using Avalonia;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulseAPK.Avalonia;

class Program
{
    internal static SingleInstanceBridge? InstanceBridge { get; private set; }
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            using var bridge = new SingleInstanceBridge();
            if (!bridge.TryAcquire())
            {
                var path = args.Length == 2 && args[0] == "--apk" ? args[1] : null;
                if (!bridge.SendAsync(path).GetAwaiter().GetResult())
                    MessageBoxW(IntPtr.Zero, "SendaAPK ya está abierto, pero no fue posible contactar con la ventana existente. Espera unos segundos e inténtalo de nuevo.", "SendaAPK", 0x30);
                return;
            }
            InstanceBridge = bridge;
            try { Run(args); }
            finally { InstanceBridge = null; }
            return;
        }
        Run(args);
    }

    private static void Run(string[] args)
    {
        ConfigureTerminalLogging();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ConfigureTerminalLogging()
    {
        Trace.AutoFlush = true;

        if (!HasConsoleTraceListener())
        {
            Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: false));
        }

        Console.WriteLine($"PulseAPK starting at {DateTimeOffset.Now:O}");
        Trace.TraceInformation("PulseAPK trace logging is enabled.");
    }

    private static bool HasConsoleTraceListener()
    {
        foreach (TraceListener listener in Trace.Listeners)
        {
            if (listener is ConsoleTraceListener)
            {
                return true;
            }
        }

        return false;
    }
}
