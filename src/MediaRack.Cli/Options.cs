namespace MediaRack.Cli;

/// <summary>参数错误。区别于运行时异常，退出码为 2。</summary>
internal sealed class CliException(string message) : Exception(message);

/// <summary>极简参数解析：支持 <c>--key value</c>、<c>--key=value</c>、<c>--flag</c>。</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _map;

    private Options(Dictionary<string, string> map, IReadOnlyList<string> positional)
    {
        _map = map;
        Positional = positional;
    }

    public IReadOnlyList<string> Positional { get; }

    public static Options Parse(IReadOnlyList<string> args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var eq = arg.IndexOf('=');
                if (eq > 2)
                {
                    map[arg[2..eq]] = arg[(eq + 1)..];
                }
                else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    map[arg[2..]] = args[++i];
                }
                else
                {
                    map[arg[2..]] = string.Empty;
                }

                continue;
            }

            positional.Add(arg);
        }

        return new Options(map, positional);
    }

    public string? Get(string name) => _map.TryGetValue(name, out var v) ? v : null;

    public string Require(string name)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value)) throw new CliException($"缺少参数 --{name}");
        return value;
    }

    public bool Flag(string name) => _map.ContainsKey(name);
}
