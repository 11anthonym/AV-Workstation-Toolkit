namespace AVWorkstationToolkit.Domain.Workstation;

/// <summary>
/// Display names Windows registers for managed catalog applications, keyed by exact WinGet package ID. The managed
/// catalog alone grants authority; this table only lets an inventory recognize an installation from its uninstall
/// registration, the way external catalog records carry a registry detector. An entry whose ID is absent from the
/// loaded managed catalog is ignored.
/// </summary>
/// <remarks>
/// Each pattern must match the DisplayName real installers register, including look-alikes it must reject, and is
/// locked by tests/fixtures/inventory/managed-program-names.json. An application with no verified registered name
/// (Windows Terminal is MSIX-only, and tftpd64 has no confirmed name) has no entry and is recognized by WinGet ID only.
/// </remarks>
public static class ManagedApplicationDetectors
{
    public static IReadOnlyDictionary<string, string> Patterns { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["7zip.7zip"] = @"^7-Zip \d+\.\d+",
        ["Notepad++.Notepad++"] = @"^Notepad\+\+(?: \([^()]*\))?$",
        ["Microsoft.VisualStudioCode"] = @"^Microsoft Visual Studio Code(?: \((?:User|System)\))?$",
        ["Microsoft.PowerShell"] = @"^PowerShell 7-(?:x64|x86|arm64)$",
        ["ShareX.ShareX"] = @"^ShareX$",
        ["Microsoft.PowerToys"] = @"^PowerToys(?: \(Preview\))?(?: (?:x64|ARM64))?$",
        ["JAMSoftware.TreeSize.Free"] = @"^TreeSize Free(?![A-Za-z])",
        ["VideoLAN.VLC"] = @"^VLC media player$",
        ["JGraph.Draw"] = @"^draw\.io(?: \d+(?:\.\d+)*)?$",
        ["PuTTY.PuTTY"] = @"^PuTTY release \d",
        ["mRemoteNG.mRemoteNG"] = @"^mRemoteNG$",
        ["Mobatek.MobaXterm"] = @"^MobaXterm(?: Home Edition)?$",
        ["Famatech.AdvancedIPScanner"] = @"^Advanced IP Scanner \d",
        ["Microsoft.RemoteDesktopClient"] = @"^Remote Desktop$",
        ["RealVNC.VNCViewer"] = @"^(?:RealVNC|VNC) Viewer \d",
        ["WiresharkFoundation.Wireshark"] = @"^Wireshark \d",
        ["Insecure.Nmap"] = @"^Nmap \d",
        ["Git.Git"] = @"^Git(?: version \d+(?:\.\d+)*)?$",
        ["GitHub.cli"] = @"^GitHub CLI$",
        ["DBBrowserForSQLite.DBBrowserForSQLite"] = @"^DB Browser for SQLite$",
        ["Python.Launcher"] = @"^Python Launcher$",
        ["Python.Python.3.11"] = @"^Python 3\.11\.\d+ \((?:64-bit|32-bit|ARM64)\)$",
        ["Microsoft.DotNet.SDK.8"] = @"^Microsoft \.NET SDK 8\.\d+\.\d+ \((?:x64|x86|arm64)\)$",
        ["DominikReichl.KeePass"] = @"^KeePass Password Safe 2\.\d",
        ["voidtools.Everything"] = @"^Everything \d",
        ["Google.Chrome"] = @"^Google Chrome$",
        ["Adobe.Acrobat.Reader.32-bit"] = @"^Adobe Acrobat Reader(?: DC)?(?: - [A-Za-z]+(?: [A-Za-z]+)*)?$",
        ["Mozilla.Firefox"] = @"^Mozilla Firefox(?: \d+(?:\.\d+)*)? \((?:x64|x86|ARM64|aarch64)(?: [A-Za-z]{2,3}(?:-[A-Za-z]{2,4})?)?\)$"
    };
}
