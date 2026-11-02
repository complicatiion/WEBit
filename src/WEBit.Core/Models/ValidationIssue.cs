namespace WEBit.Core.Models;

public enum IssueLevel
{
    Ok,
    Info,
    Warning,
    Error
}

/// <summary>Result of a validation or environment check.</summary>
public sealed class ValidationIssue
{
    public IssueLevel Level { get; set; }

    public LocalizedMessage Message { get; set; } = new();

    /// <summary>Optional field reference, e.g. "binding:0" or "root".</summary>
    public string? Field { get; set; }

    public static ValidationIssue Error(string code, string? field = null, params object?[] args) =>
        new() { Level = IssueLevel.Error, Message = LocalizedMessage.Of(code, args), Field = field };

    public static ValidationIssue Warning(string code, string? field = null, params object?[] args) =>
        new() { Level = IssueLevel.Warning, Message = LocalizedMessage.Of(code, args), Field = field };

    public static ValidationIssue Info(string code, string? field = null, params object?[] args) =>
        new() { Level = IssueLevel.Info, Message = LocalizedMessage.Of(code, args), Field = field };

    public static ValidationIssue Ok(string code, string? field = null, params object?[] args) =>
        new() { Level = IssueLevel.Ok, Message = LocalizedMessage.Of(code, args), Field = field };

    public override string ToString() => $"{Level}: {Message}";
}
