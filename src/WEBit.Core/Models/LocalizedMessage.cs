using System.Globalization;

namespace WEBit.Core.Models;

/// <summary>Language-neutral message (code + args); the UI translates it.</summary>
public sealed class LocalizedMessage
{
    public string Code { get; set; } = "";

    public string[] Args { get; set; } = [];

    public static LocalizedMessage Of(string code, params object?[] args) => new()
    {
        Code = code,
        Args = args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? "").ToArray()
    };

    public override string ToString() => Args.Length == 0 ? Code : $"{Code}({string.Join(", ", Args)})";
}

/// <summary>Message codes. UI texts use the prefix "Msg".</summary>
public static class MessageCodes
{
    public const string NameRequired = "NameRequired";
    public const string RootRequired = "RootRequired";
    public const string RootMissing = "RootMissing";
    public const string RootNoAccess = "RootNoAccess";
    public const string RedirectTargetInvalid = "RedirectTargetInvalid";
    public const string NoBinding = "NoBinding";
    public const string InvalidAddress = "InvalidAddress";
    public const string InvalidPort = "InvalidPort";
    public const string InvalidHost = "InvalidHost";
    public const string InvalidNetwork = "InvalidNetwork";
    public const string InvalidPublicUrl = "InvalidPublicUrl";
    public const string WildcardBinding = "WildcardBinding";
    public const string LoopbackBinding = "LoopbackBinding";
    public const string PortAvoided = "PortAvoided";
    public const string PortOutsideRange = "PortOutsideRange";
    public const string PortReserved = "PortReserved";
    public const string PortInUse = "PortInUse";
    public const string AddressNotLocal = "AddressNotLocal";
    public const string BindingConflict = "BindingConflict";
    public const string BindingOverlap = "BindingOverlap";
    public const string DuplicateBinding = "DuplicateBinding";
    public const string BindingOk = "BindingOk";
    public const string EngineNotAvailable = "EngineNotAvailable";
    public const string EngineNotInstalled = "EngineNotInstalled";
    public const string EngineStartFailed = "EngineStartFailed";
    public const string EngineAdminPortInUse = "EngineAdminPortInUse";
    public const string EngineStopped = "EngineStopped";
    public const string EngineLoadFailed = "EngineLoadFailed";
    public const string EngineDisabled = "EngineDisabled";
    public const string ModuleNotInstalled = "ModuleNotInstalled";
    public const string ModuleStartFailed = "ModuleStartFailed";
    public const string ModuleExited = "ModuleExited";
    public const string VcRuntimeMissing = "VcRuntimeMissing";
    public const string PhpUnavailable = "PhpUnavailable";
    public const string PhpVersionMissing = "PhpVersionMissing";
    public const string PhpPoolFull = "PhpPoolFull";
    public const string DownloadResolving = "DownloadResolving";
    public const string DownloadRunning = "DownloadRunning";
    public const string DownloadVerifying = "DownloadVerifying";
    public const string DownloadInstalling = "DownloadInstalling";
    public const string DownloadCompleted = "DownloadCompleted";
    public const string DownloadFailed = "DownloadFailed";
    public const string DownloadChecksumMismatch = "DownloadChecksumMismatch";
    public const string ProxyTargetInvalid = "ProxyTargetInvalid";
    public const string AppNoEntry = "AppNoEntry";
    public const string AppModuleMissing = "AppModuleMissing";
    public const string AppInstalling = "AppInstalling";
    public const string AppExited = "AppExited";
    public const string AppNoPort = "AppNoPort";
    public const string AppNotListening = "AppNotListening";
    public const string HttpsDisabled = "HttpsDisabled";
    public const string TlsMismatch = "TlsMismatch";
    public const string ModulePort = "ModulePort";
    public const string DbInitFailed = "DbInitFailed";
    public const string DbSecretMissing = "DbSecretMissing";
}
