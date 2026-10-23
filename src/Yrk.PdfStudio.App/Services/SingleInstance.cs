using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Yrk.PdfStudio.App.Services;

/// <summary>
/// Keeps one window per user: opening a file from Explorer while the app runs sends the path to the
/// running app (which opens it in a new tab) instead of starting a second copy. The pipe exists only on
/// this computer and only the same user can connect to it (<see cref="PipeOptions.CurrentUserOnly"/>).
/// </summary>
internal static class SingleInstance
{
    private const int MaxMessageLength = 64 * 1024;
    private static readonly string Id = "YRK.EasyPdf." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    private static Mutex? s_mutex;

    /// <summary>True for the first copy of the app for this user (it then listens for the others).</summary>
    public static bool TryBecomeFirst()
    {
        s_mutex = new Mutex(initiallyOwned: true, @"Local\" + Id, out var createdNew);
        IsFirst = createdNew;
        return createdNew;
    }

    /// <summary>This copy owns the pipe (later copies send their files to it).</summary>
    public static bool IsFirst { get; private set; }

    /// <summary>Hands the command line to the running copy. False when it could not be reached.</summary>
    public static bool SendToFirst(string[] args)
    {
        // Files are passed with full paths: the running copy has another working folder.
        var message = string.Join('\n', args.Select(arg => arg.StartsWith("--", StringComparison.Ordinal) ? arg : Path.GetFullPath(arg)));
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            AllowSetForegroundWindow(AsfwAny); // Lets the running window come to the front.
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Waits for later copies in the background and passes each one's arguments on.</summary>
    public static void Listen(Action<string[]> received) => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await server.ReadAsync(chunk)) > 0 && buffer.Length < MaxMessageLength)
                {
                    buffer.Write(chunk, 0, read);
                }

                var args = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                received(args);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.LogMessage("Single instance pipe: " + ex.Message);
                await Task.Delay(1000);
            }
        }
    });

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
