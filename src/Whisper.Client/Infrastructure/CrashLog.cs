using System.IO;
using Whisper.Client.Services;

namespace Whisper.Client.Infrastructure;

/// <summary>
/// Appends unhandled exceptions to a file next to the user's profiles. A desktop app that
/// dies without a trace leaves nothing to diagnose, and the dialog alone loses the stack.
/// </summary>
public static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(ProfileStore.DefaultDirectory(), "crash.log");

    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(
                FilePath,
                $"""
                 ===== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} =====
                 {exception}

                 """);
        }
        catch (Exception)
        {
            // Failing to record a crash must not itself crash the handler.
        }
    }
}
