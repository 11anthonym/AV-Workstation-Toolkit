@{
    # This is the complete package universe accepted by the deployment and
    # maintenance scripts. Keep security, device-management, VPN, and corporate
    # remote-support products out of this file.
    Packages = @(
        @{ Profile='Standard';  Name='7-Zip';                    Id='7zip.7zip';                                Vendor='7-Zip'; Risk='None';     Note='Archive utility' }
        @{ Profile='Standard';  Name='Notepad++';                Id='Notepad++.Notepad++';                      Vendor='Notepad++'; Risk='None'; Note='Text editor' }
        @{ Profile='Standard';  Name='Visual Studio Code';       Id='Microsoft.VisualStudioCode';               Vendor='Microsoft'; Risk='None'; Note='Code/text editor' }
        @{ Profile='Standard';  Name='PowerShell 7';             Id='Microsoft.PowerShell';                     Vendor='Microsoft'; Risk='None'; Note='Modern PowerShell runtime' }
        @{ Profile='Standard';  Name='Windows Terminal';         Id='Microsoft.WindowsTerminal';                Vendor='Microsoft'; Risk='None'; Note='Terminal' }
        @{ Profile='Standard';  Name='ShareX';                   Id='ShareX.ShareX';                            Vendor='ShareX'; Risk='None';     Note='Screen capture and documentation' }
        @{ Profile='Standard';  Name='PowerToys';                Id='Microsoft.PowerToys';                      Vendor='Microsoft'; Risk='None'; Note='Desktop utilities' }
        @{ Profile='Standard';  Name='TreeSize Free';            Id='JAMSoftware.TreeSize.Free';                Vendor='JAM Software'; Risk='None'; Note='Disk inspection' }
        @{ Profile='Standard';  Name='VLC media player';         Id='VideoLAN.VLC';                             Vendor='VideoLAN'; Risk='None';   Note='Media and test-pattern playback' }
        @{ Profile='Standard';  Name='draw.io Desktop';          Id='JGraph.Draw';                              Vendor='JGraph'; Risk='None';     Note='Signal-flow diagrams' }

        @{ Profile='Field';     Name='PuTTY';                    Id='PuTTY.PuTTY';                              Vendor='PuTTY'; Risk='None';       Note='SSH and serial client' }
        @{ Profile='Field';     Name='mRemoteNG';                Id='mRemoteNG.mRemoteNG';                      Vendor='mRemoteNG'; Risk='None';   Note='Remote-session client' }
        @{ Profile='Field';     Name='MobaXterm';                Id='Mobatek.MobaXterm';                        Vendor='Mobatek'; Risk='None';     Note='SSH and serial client' }
        @{ Profile='Field';     Name='Advanced IP Scanner';      Id='Famatech.AdvancedIPScanner';               Vendor='Famatech'; Risk='None';    Note='Use only on authorized networks' }
        @{ Profile='Field';     Name='Remote Desktop';           Id='Microsoft.RemoteDesktopClient';            Vendor='Microsoft'; Risk='None';   Note='Microsoft RDP client' }
        @{ Profile='Field';     Name='RealVNC Viewer';           Id='RealVNC.VNCViewer';                        Vendor='RealVNC'; Risk='None';     Deployment='ManualHold'; Maintenance='Hold'; Note='Viewer only; winget download is stale/broken; hold installs and updates; never substitute VNC server' }
        @{ Profile='Field';     Name='Wireshark';                Id='WiresharkFoundation.Wireshark';            Vendor='Wireshark Foundation'; Risk='Driver'; Note='Packet capture with Npcap; never install WinPcap' }
        @{ Profile='Field';     Name='Nmap';                     Id='Insecure.Nmap';                            Vendor='Nmap Project'; Risk='Driver'; Note='Npcap and scanning; authorized networks only' }
        @{ Profile='Field';     Name='tftpd64';                  Id='PJO2.tftpd64';                             Vendor='Tftpd64'; Risk='Listener'; Maintenance='Hold'; Note='On-demand only; never use service edition; winget update metadata requires manual review' }

        @{ Profile='Developer'; Name='Git';                      Id='Git.Git';                                  Vendor='Git for Windows'; Risk='None'; Note='Git client' }
        @{ Profile='Developer'; Name='GitHub CLI';               Id='GitHub.cli';                               Vendor='GitHub'; Risk='None'; Note='GitHub command-line client' }
        @{ Profile='Developer'; Name='DB Browser for SQLite';    Id='DBBrowserForSQLite.DBBrowserForSQLite';     Vendor='DB Browser for SQLite'; Risk='None'; Note='SQLite inspection' }
        @{ Profile='Developer'; Name='Python Launcher';          Id='Python.Launcher';                          Vendor='Python Software Foundation'; Risk='None'; Note='Developer option; install only for an identified Python project' }
        @{ Profile='Developer'; Name='Python 3.11';              Id='Python.Python.3.11';                       Vendor='Python Software Foundation'; Risk='None'; Note='Compatibility runtime for identified projects; not tied to VirtualBox' }
        @{ Profile='Developer'; Name='.NET SDK 8';               Id='Microsoft.DotNet.SDK.8';                   Vendor='Microsoft'; Risk='None'; Note='Developer SDK; install only when an identified project requires .NET 8' }

        @{ Profile='Optional';  Name='KeePass';                  Id='DominikReichl.KeePass';                    Vendor='Dominik Reichl'; Risk='None'; Note='Credential database client' }
        @{ Profile='Optional';  Name='Everything';               Id='voidtools.Everything';                     Vendor='voidtools'; Risk='Service'; Note='Optional indexing service' }
        @{ Profile='Optional';  Name='Google Chrome';            Id='Google.Chrome';                            Vendor='Google'; Risk='None'; Note='Optional secondary browser' }
        @{ Profile='Optional';  Name='Adobe Acrobat Reader';     Id='Adobe.Acrobat.Reader.32-bit';              Vendor='Adobe'; Risk='None'; Note='Optional PDF reader' }
    )

    # Defense in depth: a future allowlist edit containing one of these terms
    # fails closed before winget can run an install or upgrade command.
    ForbiddenPattern = '(?i)BitLocker|CrowdStrike|Falcon|Defender|Sentinel|Sophos|McAfee|Symantec|CarbonBlack|Cylance|Forti(Client)?|AnyConnect|SecureClient|Intune|CompanyPortal|SCCM|TeamViewer|TightVNC'
}
