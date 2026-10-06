using System.Globalization;

namespace OledMirror.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message, Exception? Exception = null)
{
    public string Format()
    {
        string level = Level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "AVS",
            _ => "ERR",
        };
        string text = $"{Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} [{Category}] {Message}";
        return Exception is null ? text : $"{text} - {Exception.GetType().Name}: {Exception.Message}";
    }

    public override string ToString() => Format();
}

public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>
/// Logger simples e seguro entre threads. Um sink que falha nunca derruba quem
/// esta registrando: o log existe para diagnosticar problemas, nao para causa-los.
/// </summary>
public sealed class Logger
{
    private readonly object _gate = new();
    private ILogSink[] _sinks = Array.Empty<ILogSink>();

    /// <summary>Logger que descarta tudo.</summary>
    public static Logger Null { get; } = new() { MinimumLevel = (LogLevel)int.MaxValue };

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public void AddSink(ILogSink sink)
    {
        lock (_gate) _sinks = _sinks.Append(sink).ToArray();
    }

    public void RemoveSink(ILogSink sink)
    {
        lock (_gate) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
    }

    public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public void Warning(string category, string message, Exception? exception = null) => Write(LogLevel.Warning, category, message, exception);
    public void Error(string category, string message, Exception? exception = null) => Write(LogLevel.Error, category, message, exception);

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (!IsEnabled(level)) return;

        var entry = new LogEntry(DateTime.Now, level, category, message, exception);
        foreach (ILogSink sink in _sinks)
        {
            try { sink.Write(entry); }
            catch (Exception) { /* sink com problema nao pode afetar a aplicacao */ }
        }
    }
}

public sealed class ConsoleLogSink : ILogSink
{
    private readonly object _gate = new();

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            if (entry.Level >= LogLevel.Warning) Console.Error.WriteLine(entry.Format());
            else Console.WriteLine(entry.Format());
        }
    }
}

/// <summary>Guarda as ultimas entradas em memoria e avisa a interface a cada nova.</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();

    public MemoryLogSink(int capacity = 1000)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    public event Action<LogEntry>? EntryWritten;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
        EntryWritten?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}

/// <summary>Anexa as entradas num arquivo texto.</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public FileLogSink(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        Path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public string Path { get; }

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{entry.Timestamp:yyyy-MM-dd} {entry.Format()}");
            if (entry.Exception is not null) _writer.WriteLine(entry.Exception);
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}