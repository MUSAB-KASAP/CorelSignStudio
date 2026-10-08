using System.Text;

namespace CorelSignStudio.Storage;

public sealed class FileLogWriter
{
    private readonly object _gate = new();

    public FileLogWriter(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        LogPath = Path.Combine(logDirectory, $"corel-sign-studio-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogPath { get; }

    public void Write(string message, Exception? exception = null)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" | ")
            .Append(message);
        if (exception is not null)
        {
            entry.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            File.AppendAllText(LogPath, entry.AppendLine().ToString(), Encoding.UTF8);
        }
    }
}
