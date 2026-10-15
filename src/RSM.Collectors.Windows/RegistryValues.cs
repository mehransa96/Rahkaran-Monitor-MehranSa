using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RSM.Collectors.Windows;

/// <summary>A whitelisted HKLM value (value name null = "does the key exist").</summary>
public sealed record RegistryValueId(string Key, string? Value);

/// <summary>
/// The only registry locations RSM reads (§9.2, RULES.md §1). Anything else is rejected before a key is opened.
/// </summary>
public static class RegistryWhitelist
{
    public static readonly RegistryValueId PrioritySeparation = new(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
    public static readonly RegistryValueId CbsRebootPending = new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", null);
    public static readonly RegistryValueId WindowsUpdateRebootRequired = new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", null);
    public static readonly RegistryValueId PendingFileRenames = new(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations");
    public static readonly RegistryValueId ActivePowerScheme = new(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme");
    public static readonly RegistryValueId DefenderRealtimeDisabled = new(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring");
    public static readonly RegistryValueId DefenderExcludedPaths = new(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths", "*");
    public static readonly RegistryValueId DefenderExcludedProcesses = new(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Processes", "*");

    public static IReadOnlyList<RegistryValueId> All { get; } =
        [PrioritySeparation, CbsRebootPending, WindowsUpdateRebootRequired, PendingFileRenames, ActivePowerScheme, DefenderRealtimeDisabled, DefenderExcludedPaths, DefenderExcludedProcesses];

    public static bool IsAllowed(RegistryValueId id) => All.Contains(id);
}

/// <summary>
/// Reads whitelisted HKLM values locally or through the Remote Registry service (current user's rights), always with
/// read-only <c>OpenSubKey(name)</c>. Value "*" returns the value <em>names</em> of the key (Defender exclusions are
/// stored as value names).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryReader : IRegistryValues, IDisposable
{
    private readonly RegistryKey _hklm;

    private RegistryReader(RegistryKey hklm) => _hklm = hklm;

    /// <summary>Local machine (null) or a remote host; throws IOException/UnauthorizedAccessException when unreachable.</summary>
    public static RegistryReader Open(string? computer) => new(computer is null
        ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
        : RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, computer, RegistryView.Registry64));

    public object? Read(RegistryValueId id)
    {
        Guard(id);
        using var key = _hklm.OpenSubKey(id.Key);
        if (key is null)
        {
            return null;
        }

        return id.Value == "*" ? key.GetValueNames() : key.GetValue(id.Value);
    }

    public bool KeyExists(RegistryValueId id)
    {
        Guard(id);
        using var key = _hklm.OpenSubKey(id.Key);
        return key is not null;
    }

    private static void Guard(RegistryValueId id)
    {
        if (!RegistryWhitelist.IsAllowed(id))
        {
            throw new InvalidOperationException($"Registry location not in the whitelist: {id.Key}");
        }
    }

    public void Dispose() => _hklm.Dispose();
}
