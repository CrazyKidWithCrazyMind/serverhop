using System.Runtime.InteropServices;
using System.Windows;

namespace ServerHop;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        if (args.Length == 0)
            return;

        var cmd = args[0].ToLowerInvariant();

        // Hidden CLI modes: no window is shown, results go to stdout + a report file.
        if (cmd is "--selftest" or "--help" or "--version")
        {
            AttachConsole(AttachParentProcess);
            int code = Core.Cli.Run(args);
            Shutdown(code);
        }
    }
}
