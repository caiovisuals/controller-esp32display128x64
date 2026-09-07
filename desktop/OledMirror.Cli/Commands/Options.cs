namespace OledMirror.Cli.Commands;

/// <summary>Leitor simples de argumentos --chave valor / --flag</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public Options(IEnumerable<string> args)
    {
        string? pending = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) _values[pending] = null;
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                _values[pending] = arg;
                pending = null;
            }
        }
        if (pending is not null) _values[pending] = null;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out string? v) ? v : null;

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out int v) ? v : fallback;

    public double GetDouble(string key, double fallback)
        => double.TryParse(Get(key), System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

    public T GetEnum<T>(string key, T fallback) where T : struct, Enum
        => Enum.TryParse(Get(key), ignoreCase: true, out T v) ? v : fallback;
}