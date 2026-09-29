namespace Cmux.Core;

public static class GuiCommandProtocol
{
    public static string PipeName => InstanceScope.Qualify("cmux-gui-" + Environment.UserName);
}

public sealed record GuiCommandRequest(string Command, string? Argument = null, string? Directory = null);
public sealed record GuiCommandResponse(bool Ok, string? Message = null, string[]? Items = null);
