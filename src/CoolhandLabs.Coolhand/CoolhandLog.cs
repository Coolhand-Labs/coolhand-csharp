namespace CoolhandLabs;

/// <summary>Severity of an SDK log message.</summary>
public enum CoolhandLogLevel
{
    /// <summary>Progress messages; only produced when a client is not <c>Silent</c>.</summary>
    Info,
    /// <summary>Warnings (coolhand-node's <c>console.warn</c>).</summary>
    Warning,
    /// <summary>Errors (coolhand-node's <c>console.error</c>).</summary>
    Error,
}

/// <summary>
/// Where the SDK's own messages go. By default progress messages go to standard output and warnings
/// and errors to standard error, as coolhand-node's <c>console</c> calls do. Progress messages are
/// produced only when a client is not silent (the default is silent); warnings and errors that
/// coolhand-node prints regardless of <c>silent</c> are always produced. Point <see cref="Sink"/> at
/// your logger (or set it to null to drop everything).
/// </summary>
public static class CoolhandLog
{
    private static readonly Action<CoolhandLogLevel, string> ConsoleSink = (level, message) =>
    {
        if (level == CoolhandLogLevel.Info) Console.Out.WriteLine(message);
        else Console.Error.WriteLine(message);
    };

    /// <summary>Receives every message; null drops them. Defaults to the console.</summary>
    public static Action<CoolhandLogLevel, string>? Sink { get; set; } = ConsoleSink;

    /// <summary>Restores the console sink.</summary>
    public static void UseConsole() => Sink = ConsoleSink;

    internal static void Write(CoolhandLogLevel level, string message)
    {
        try
        {
            Sink?.Invoke(level, message);
        }
        catch
        {
            // A logging sink must never break the host application.
        }
    }
}
