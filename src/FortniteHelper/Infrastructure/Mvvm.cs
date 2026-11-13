using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace FortniteHelper.Infrastructure;

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Atomic JSON persistence (write to temp file, then replace) so a crash never corrupts data.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string DataDir { get; } = CreateDataDir();

    static string CreateDataDir()
    {
        // LOOTLOG_DATA_DIR: run on another data folder (snapshot mode uses a copy, so the real data is never touched).
        var dir = Environment.GetEnvironmentVariable("LOOTLOG_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FortniteHelper");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string PathOf(string file) => Path.Combine(DataDir, file);

    public static T Load<T>(string file) where T : new()
    {
        var path = PathOf(file);
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var json = File.ReadAllText(candidate);
                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null) return value;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {candidate}: {ex.Message}");
            }
        }
        return new T();
    }

    public static void Save<T>(string file, T value)
    {
        var path = PathOf(file);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}

/// <summary>Tiny rolling log file in the data folder, for diagnosing detection problems.</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = JsonStore.PathOf("app.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
