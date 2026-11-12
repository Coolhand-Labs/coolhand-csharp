namespace CoolhandLabs.Internal;

/// <summary>A client's view of <see cref="CoolhandLog"/>, honouring its <c>silent</c> setting.</summary>
internal sealed class Log
{
    public Log(bool silent) => Silent = silent;

    public bool Silent { get; }

    /// <summary>A progress message, dropped when silent.</summary>
    public void Info(string message)
    {
        if (!Silent) CoolhandLog.Write(CoolhandLogLevel.Info, message);
    }

    public void Info(Func<string> message)
    {
        if (!Silent) CoolhandLog.Write(CoolhandLogLevel.Info, message());
    }

    /// <summary>A warning shown only when not silent.</summary>
    public void WarnUnlessSilent(string message)
    {
        if (!Silent) CoolhandLog.Write(CoolhandLogLevel.Warning, message);
    }

    /// <summary>A warning shown whatever <c>silent</c> says.</summary>
    public static void Warn(string message) => CoolhandLog.Write(CoolhandLogLevel.Warning, message);

    /// <summary>An error shown whatever <c>silent</c> says.</summary>
    public static void Error(string message) => CoolhandLog.Write(CoolhandLogLevel.Error, message);
}
