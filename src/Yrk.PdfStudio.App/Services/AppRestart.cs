using System.Diagnostics;

namespace Yrk.PdfStudio.App.Services;

internal static class AppRestart
{
    private const string WaitArgument = "--restart-after";

    internal static bool Start()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No executable path."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(WaitArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start);
        return process is not null;
    }

    // The child must wait before the single-instance check, otherwise it would exit immediately.
    internal static bool WaitForPreviousInstance(ref string[] args)
    {
        if (args.Length != 2 || args[0] != WaitArgument || !int.TryParse(args[1], out var pid)) return true;
        args = [];
        try
        {
            using var previous = Process.GetProcessById(pid);
            return previous.WaitForExit(60_000);
        }
        catch (ArgumentException) { return true; } // Already exited.
    }
}
