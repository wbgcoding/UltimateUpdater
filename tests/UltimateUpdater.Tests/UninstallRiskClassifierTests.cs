using System.Diagnostics;
using System.Xml.Linq;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>
/// The uninstaller risk rating (docs/DESIGN.md, "Uninstaller risk rating"): one hand-rated fixture of real-looking
/// entries across every rule and every decoy, the normalization and matching helpers, the fail-closed paths, and
/// invariants over hostile input.
/// </summary>
public class UninstallRiskClassifierTests
{
    private const string WindowsDir = @"C:\Windows";

    private const CleanupRiskLevel R = CleanupRiskLevel.Risky;
    private const CleanupRiskLevel C = CleanupRiskLevel.Considered;
    private const CleanupRiskLevel S = CleanupRiskLevel.Safe;

    private sealed record Case(string Category, UninstallRiskInput Input, CleanupRiskLevel Level, UninstallRiskReason Reason);

    private static UninstallRiskInput ProgramInput(string name, string? publisher, UninstallScope scope = UninstallScope.Machine, UninstallMethod? method = UninstallMethod.Msi) => new()
    {
        Kind = UninstallEntryKind.Program,
        DisplayName = name,
        Publisher = publisher,
        Scope = scope,
        Method = method,
        WindowsDirectory = WindowsDir,
    };

    private static Case P(string category, string name, string? publisher, CleanupRiskLevel level, UninstallRiskReason reason) =>
        new(category, ProgramInput(name, publisher), level, reason);

    private static Case PU(string category, string name, string? publisher, CleanupRiskLevel level, UninstallRiskReason reason) =>
        new(category, ProgramInput(name, publisher, UninstallScope.User), level, reason);

    private static Case Store(string category, string name, string family, string? publisher, CleanupRiskLevel level, UninstallRiskReason reason) =>
        new(category, new UninstallRiskInput
        {
            Kind = UninstallEntryKind.StoreApp,
            DisplayName = name,
            PackageFamilyName = family,
            Publisher = publisher,
            Scope = UninstallScope.User,
            Method = UninstallMethod.StoreApp,
            WindowsDirectory = WindowsDir,
        }, level, reason);

    private static Case Pkg(string category, string source, string id, string name, bool? required, CleanupRiskLevel level, UninstallRiskReason reason) =>
        new(category, new UninstallRiskInput
        {
            Kind = UninstallEntryKind.Package,
            SourceId = source,
            PackageId = id,
            DisplayName = name,
            RequiredByOthers = required,
            Scope = UninstallScope.User,
            Method = UninstallMethod.Winget,
            WindowsDirectory = WindowsDir,
        }, level, reason);

    private const string MicrosoftCertificate = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    private static readonly Dictionary<string, CleanupRiskLevel> MinimumLevelOf = new()
    {
        ["security"] = R, ["accessibility"] = R, ["dataloss"] = R, ["companyaccess"] = R, ["runtime"] = R, ["hardware"] = R,
        ["microsoft-risky"] = R, ["driver"] = R, ["update"] = R, ["pm-itself"] = R,
        ["virtualization"] = C, ["microsoft-allow"] = C, ["exception"] = C, ["remote"] = C, ["sync"] = C, ["game"] = C,
        ["input"] = C, ["devruntime"] = C, ["oem"] = C, ["runtime-generic"] = C, ["interactive"] = C, ["unknown"] = C,
        ["incomplete"] = C, ["storedata"] = C, ["needed"] = C,
        ["ordinary"] = S, ["decoy"] = S,
    };

    private static readonly Case[] Fixture =
    [
        // Security and encryption.
        P("security", "Bitdefender Total Security", "Bitdefender", R, UninstallRiskReason.Security),
        P("security", "Kaspersky Standard", "AO Kaspersky Lab", R, UninstallRiskReason.Security),
        P("security", "ESET NOD32 Antivirus 16", "ESET, spol. s r.o.", R, UninstallRiskReason.Security),
        P("security", "Norton 360", "NortonLifeLock Inc.", R, UninstallRiskReason.Security),
        P("security", "Avast Free Antivirus", "Avast Software s.r.o.", R, UninstallRiskReason.Security),
        P("security", "AVG Internet Security", "AVG Technologies", R, UninstallRiskReason.Security),
        P("security", "McAfee LiveSafe", "McAfee, LLC", R, UninstallRiskReason.Security),
        P("security", "Malwarebytes version 4.6.8", "Malwarebytes", R, UninstallRiskReason.Security),
        P("security", "Windows Agent", "Sentinel Labs, Inc.", R, UninstallRiskReason.Security),
        P("security", "CrowdStrike Windows Sensor", "CrowdStrike, Inc.", R, UninstallRiskReason.Security),
        P("security", "VeraCrypt", "IDRIX", R, UninstallRiskReason.Security),
        P("security", "Gpg4win (4.2.0)", "The Gpg4win Project", R, UninstallRiskReason.Security),
        P("security", "Dell Encryption", "Dell Inc.", R, UninstallRiskReason.Security),
        P("security", "HP Wolf Security", "HP Inc.", R, UninstallRiskReason.Security),
        P("security", "Acronis Cyber Protect Agent", "Acronis International GmbH", R, UninstallRiskReason.Security),
        P("security", "Acme Endpoint Protection Agent", "Acme Widgets Ltd", R, UninstallRiskReason.Security),
        P("security", "Acme Firewall Manager", "Acme Widgets Ltd", R, UninstallRiskReason.Security),
        P("security", "Microsoft Defender Antivirus", "Microsoft Corporation", R, UninstallRiskReason.Security),
        P("security", "Panda Dome", "Panda Security", R, UninstallRiskReason.Security),
        P("security", "Setup Helper", "Kaspersky Lab", R, UninstallRiskReason.Security),
        P("security", "Festplatten Verschlüsselung 2", "Acme Widgets Ltd", R, UninstallRiskReason.Security),
        P("security", "YubiKey Smart Card Minidriver", "Yubico", R, UninstallRiskReason.Security),
        P("security", "BITDEFENDER TOTAL SECURITY", "BITDEFENDER", R, UninstallRiskReason.Security),

        // Accessibility.
        P("accessibility", "NVDA 2023.3", "NV Access", R, UninstallRiskReason.Accessibility),
        P("accessibility", "JAWS 2024", "Freedom Scientific", R, UninstallRiskReason.Accessibility),
        P("accessibility", "ZoomText 2023", "Vispero", R, UninstallRiskReason.Accessibility),
        P("accessibility", "Fusion 2023", "Freedom Scientific Inc.", R, UninstallRiskReason.Accessibility),
        P("accessibility", "Dragon NaturallySpeaking 15", "Nuance Communications, Inc.", R, UninstallRiskReason.Accessibility),
        P("accessibility", "Dolphin SuperNova Magnifier", "Dolphin Computer Access", R, UninstallRiskReason.Accessibility),
        P("accessibility", "Communicator 5", "Tobii Dynavox", R, UninstallRiskReason.Accessibility),

        // Data that goes with the program.
        P("dataloss", "Steam", "Valve Corporation", R, UninstallRiskReason.DataLoss),
        P("dataloss", "Docker Desktop", "Docker Inc.", R, UninstallRiskReason.DataLoss),
        P("dataloss", "Rancher Desktop", "SUSE LLC", R, UninstallRiskReason.DataLoss),
        P("dataloss", "BlueStacks 5", "now.gg, Inc.", R, UninstallRiskReason.DataLoss),
        P("dataloss", "XAMPP", "Apache Friends", R, UninstallRiskReason.DataLoss),
        Store("dataloss", "CanonicalGroupLimited.Ubuntu22.04LTS", "CanonicalGroupLimited.Ubuntu22.04LTS_79rhkp1fndgsc", "CN=Canonical Group Limited", R, UninstallRiskReason.DataLoss),
        Store("dataloss", "TheDebianProject.DebianGNULinux", "TheDebianProject.DebianGNULinux_76v4gfsz19hv4", "CN=Debian", R, UninstallRiskReason.DataLoss),
        Store("dataloss", "KaliLinux.54290C8133FEE", "KaliLinux.54290C8133FEE_ey8k8hqnwqnmg", "CN=Kali", R, UninstallRiskReason.DataLoss),
        Store("dataloss", "Microsoft.MinecraftUWP", "Microsoft.MinecraftUWP_8wekyb3d8bbwe", MicrosoftCertificate, R, UninstallRiskReason.DataLoss),
        Store("dataloss", "MicrosoftCorporationII.WindowsSubsystemForLinux", "MicrosoftCorporationII.WindowsSubsystemForLinux_8wekyb3d8bbwe", MicrosoftCertificate, R, UninstallRiskReason.DataLoss),

        // Company management and access.
        P("companyaccess", "Cisco AnyConnect Secure Mobility Client", "Cisco Systems, Inc.", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "GlobalProtect", "Palo Alto Networks", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "FortiClient", "Fortinet Inc", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "Zscaler", "Zscaler Inc.", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "Splunk Universal Forwarder", "Splunk, Inc.", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "Workspace ONE Intelligent Hub", "VMware, Inc.", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "Qualys Cloud Agent", "Qualys, Inc.", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "NinjaRMMAgent", "NinjaOne", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "Cisco Secure Endpoint", "Cisco Systems, Inc.", R, UninstallRiskReason.CompanyAccess),
        P("companyaccess", "Tanium Client 7.4", "Tanium Inc.", R, UninstallRiskReason.CompanyAccess),
        P("ordinary", "Cisco Webex Meetings", "Cisco Systems, Inc.", S, UninstallRiskReason.Ordinary),

        // Shared runtimes.
        P("runtime", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.6161", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Windows Desktop Runtime - 8.0.1 (x64)", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Office Access database engine 2007 (English)", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Office 2010 Primary Interop Assemblies", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("microsoft-allow", "Microsoft Office Professional Plus 2019", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("runtime", "Microsoft .NET Runtime - 8.0.1 (x64)", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft .NET SDK 8.0.100 (x64)", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft ASP.NET Core 8.0.1 - Shared Framework (x64)", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Edge WebView2 Runtime", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Edge", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Windows Software Development Kit - Windows 10.0.22621.755", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Microsoft Visual Studio Installer", "Microsoft Corporation", R, UninstallRiskReason.Runtime),
        P("runtime", "Vulkan Run Time Libraries 1.3.250.1", "LunarG, Inc.", R, UninstallRiskReason.Runtime),
        P("runtime", "DirectX End-User Runtimes (June 2010)", "Microsoft Corporation", R, UninstallRiskReason.Runtime),

        // Hardware.
        P("hardware", "Intel(R) Wireless Bluetooth(R)", "Intel Corporation", R, UninstallRiskReason.Hardware),
        P("hardware", "Realtek High Definition Audio Driver", "Realtek Semiconductor Corp.", R, UninstallRiskReason.Hardware),
        P("hardware", "Realtek Audio Driver", "Dell Inc.", R, UninstallRiskReason.Hardware),
        P("hardware", "IDT Audio", "IDT", R, UninstallRiskReason.Hardware),
        P("hardware", "Senary Audio", "Senary Audio", R, UninstallRiskReason.Hardware),
        P("hardware", "CSR Harmony Wireless Software Stack", "CSR plc", R, UninstallRiskReason.Hardware),
        P("hardware", "Atheros Client Installation Program", "Atheros Communications Inc.", R, UninstallRiskReason.Hardware),
        P("hardware", "Boot Camp Services", "Apple Inc.", R, UninstallRiskReason.Hardware),
        P("hardware", "NVIDIA Graphics Driver 546.33", "NVIDIA Corporation", R, UninstallRiskReason.Hardware),
        P("hardware", "AMD Software: Adrenalin Edition", "Advanced Micro Devices, Inc.", R, UninstallRiskReason.Hardware),
        P("hardware", "Pointing Device Setup", "Synaptics Incorporated", R, UninstallRiskReason.Hardware),
        P("hardware", "Npcap 1.79", "Nmap Project", R, UninstallRiskReason.Hardware),
        P("hardware", "Oracle VM VirtualBox Guest Additions 7.0.12", "Oracle Corporation", R, UninstallRiskReason.Hardware),
        P("hardware", "VMware Tools", "VMware, Inc.", R, UninstallRiskReason.Hardware),
        P("hardware", "Canon IJ Printer Driver", "CANON INC.", R, UninstallRiskReason.Hardware),
        P("hardware", "Wacom Tablet", "Wacom Technology Corp.", R, UninstallRiskReason.Hardware),
        P("hardware", "Elgato Wave Link", "Corsair Memory, Inc.", R, UninstallRiskReason.Hardware),
        P("hardware", "Windows Driver Package - Intel (e1dexpress) Net", "Intel", R, UninstallRiskReason.Hardware),
        P("hardware", "Qualcomm Atheros Wi-Fi", "Qualcomm", R, UninstallRiskReason.Hardware),
        P("hardware", "Chipset Device Software", "Acme Widgets Ltd", R, UninstallRiskReason.Hardware),
        P("hardware", "System Firmware Update", "Acme Widgets Ltd", R, UninstallRiskReason.Hardware),
        P("hardware", "Radeon Companion", "Advanced Micro Devices, Inc.", R, UninstallRiskReason.Hardware),
        Store("hardware", "AppUp.IntelGraphicsExperience", "AppUp.IntelGraphicsExperience_8j3eq9eme6ctt", "CN=Zzz", R, UninstallRiskReason.Hardware),
        Store("hardware", "NVIDIACorp.NVIDIAControlPanel", "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj", "CN=Zzz", R, UninstallRiskReason.Hardware),
        Store("hardware", "RealtekSemiconductorCorp.RealtekAudioControl", "RealtekSemiconductorCorp.RealtekAudioControl_dt26b99r8h8gj", "CN=Zzz", R, UninstallRiskReason.Hardware),
        Store("hardware", "DolbyLaboratories.DolbyAccess", "DolbyLaboratories.DolbyAccess_rz1tebttyb220", "CN=Zzz", R, UninstallRiskReason.Hardware),

        // Virtualization (before the Hardware vendors: Broadcom owns VMware).
        P("virtualization", "VMware Workstation", "VMware, Inc.", C, UninstallRiskReason.Virtualization),
        P("virtualization", "VMware Workstation", "Broadcom Inc.", C, UninstallRiskReason.Virtualization),
        P("virtualization", "Oracle VM VirtualBox 7.0.12", "Oracle Corporation", C, UninstallRiskReason.Virtualization),
        P("virtualization", "Vagrant", "HashiCorp", C, UninstallRiskReason.Virtualization),
        P("virtualization", "QEMU", "QEMU Project", C, UninstallRiskReason.Virtualization),
        P("virtualization", "Genymotion", "Genymobile", C, UninstallRiskReason.Virtualization),

        // Microsoft: Risky unless allowlisted.
        Store("microsoft-risky", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe", MicrosoftCertificate, R, UninstallRiskReason.Microsoft),
        Store("microsoft-risky", "Microsoft.WindowsStore", "Microsoft.WindowsStore_8wekyb3d8bbwe", MicrosoftCertificate, R, UninstallRiskReason.Microsoft),
        Store("microsoft-risky", "Microsoft.SecurityHealth", "Microsoft.SecurityHealth_cw5n1h2txyewy", "CN=Zzz", R, UninstallRiskReason.Microsoft),
        P("microsoft-risky", "Microsoft Visual Studio Community 2022", "Microsoft Corporation", R, UninstallRiskReason.Microsoft),
        P("microsoft-risky", "Microsoft Update Health Tools", "Microsoft Corporation", R, UninstallRiskReason.Microsoft),
        P("microsoft-risky", "Windows PC Health Check", "Microsoft Corporation", R, UninstallRiskReason.Microsoft),
        Store("microsoft-risky", "Contoso.Thing", string.Empty, "CN=Microsoft Corporation", R, UninstallRiskReason.Microsoft),

        // Microsoft allowlist.
        Store("microsoft-allow", "Microsoft.WindowsCalculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", MicrosoftCertificate, C, UninstallRiskReason.Microsoft),
        Store("microsoft-allow", "Microsoft.Windows.Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe", MicrosoftCertificate, C, UninstallRiskReason.Microsoft),
        Store("microsoft-allow", "Microsoft.WindowsTerminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", MicrosoftCertificate, C, UninstallRiskReason.Microsoft),
        Store("microsoft-allow", "MSTeams", "MSTeams_8wekyb3d8bbwe", MicrosoftCertificate, C, UninstallRiskReason.Microsoft),
        Store("microsoft-allow", "Microsoft.PowerShell", "Microsoft.PowerShell_8wekyb3d8bbwe", MicrosoftCertificate, C, UninstallRiskReason.DevRuntime),
        P("microsoft-allow", "Microsoft Visual Studio Code", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("microsoft-allow", "PowerToys (Preview) x64", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("microsoft-allow", "Microsoft OneDrive", "Microsoft Corporation", C, UninstallRiskReason.Sync),
        P("microsoft-allow", "PowerShell 7-x64", "Microsoft Corporation", C, UninstallRiskReason.DevRuntime),
        P("microsoft-allow", "Microsoft 365 Apps for enterprise - en-us", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("microsoft-allow", "Microsoft Teams", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("microsoft-allow", "Skype", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("microsoft-allow", "Azure Data Studio", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("microsoft-allow", "Microsoft Remote Desktop", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),

        // Considered exceptions that must not become Risky.
        P("exception", "Driver Booster 11", "IObit", C, UninstallRiskReason.OemTool),
        P("exception", "Snappy Driver Installer", "Glenn Delahoy", C, UninstallRiskReason.OemTool),
        P("exception", "McAfee WebAdvisor", "McAfee, LLC", C, UninstallRiskReason.Ordinary),
        P("exception", "Avast Secure Browser", "Avast Software s.r.o.", C, UninstallRiskReason.Ordinary),
        P("exception", "Norton Safe Web", "NortonLifeLock Inc.", C, UninstallRiskReason.Ordinary),
        P("exception", "NVIDIA GeForce Experience 3.27", "NVIDIA Corporation", C, UninstallRiskReason.OemTool),
        P("exception", "NVIDIA app", "NVIDIA Corporation", C, UninstallRiskReason.OemTool),
        P("exception", "Waves Central", "Waves Audio Ltd.", C, UninstallRiskReason.OemTool),
        P("exception", "Intel(R) Driver & Support Assistant", "Intel Corporation", C, UninstallRiskReason.OemTool),
        P("exception", "Intel Unison", "Intel Corporation", C, UninstallRiskReason.OemTool),
        P("exception", "Microsoft Edge Canary", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),
        P("exception", "Microsoft Edge Beta", "Microsoft Corporation", C, UninstallRiskReason.Microsoft),

        // Remote access and VPN.
        P("remote", "TeamViewer", "TeamViewer Germany GmbH", C, UninstallRiskReason.Remote),
        P("remote", "AnyDesk", "AnyDesk Software GmbH", C, UninstallRiskReason.Remote),
        P("remote", "OpenVPN Connect", "OpenVPN Inc.", C, UninstallRiskReason.Remote),
        P("remote", "Tailscale", "Tailscale Inc.", C, UninstallRiskReason.Remote),
        P("remote", "WireGuard", "WireGuard LLC", C, UninstallRiskReason.Remote),
        P("remote", "NordVPN", "Nord Security", C, UninstallRiskReason.Remote),
        P("remote", "Citrix Workspace 2402", "Citrix Systems, Inc.", C, UninstallRiskReason.Remote),

        // Backup and sync.
        P("sync", "Dropbox", "Dropbox, Inc.", C, UninstallRiskReason.Sync),
        P("sync", "Google Drive", "Google LLC", C, UninstallRiskReason.Sync),
        P("sync", "iCloud", "Apple Inc.", C, UninstallRiskReason.Sync),
        P("sync", "Backblaze", "Backblaze, Inc.", C, UninstallRiskReason.Sync),
        P("sync", "Acronis Cyber Protect Home Office", "Acronis International GmbH", C, UninstallRiskReason.Sync),
        P("sync", "Nextcloud", "Nextcloud GmbH", C, UninstallRiskReason.Sync),

        // Game platforms and anti-cheat.
        P("game", "Epic Games Launcher", "Epic Games, Inc.", C, UninstallRiskReason.GamePlatform),
        P("game", "Battle.net", "Blizzard Entertainment", C, UninstallRiskReason.GamePlatform),
        P("game", "GOG GALAXY", "GOG sp. z o.o.", C, UninstallRiskReason.GamePlatform),
        P("game", "Riot Vanguard", "Riot Games, Inc.", C, UninstallRiskReason.GamePlatform),
        P("game", "Ubisoft Connect", "Ubisoft", C, UninstallRiskReason.GamePlatform),
        P("game", "EA app", "Electronic Arts", C, UninstallRiskReason.GamePlatform),

        // Input methods.
        P("input", "Keyman 17.0", "SIL International", C, UninstallRiskReason.InputMethod),
        P("input", "Google Japanese Input", "Google LLC", C, UninstallRiskReason.InputMethod),

        // Programming runtimes.
        P("devruntime", "Python 3.12.1 (64-bit)", "Python Software Foundation", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Python Launcher", "Python Software Foundation", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Node.js", "Node.js Foundation", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Git version 2.43.0", "The Git Development Community", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Java 8 Update 361 (64-bit)", "Oracle Corporation", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Eclipse Temurin JDK with Hotspot 17.0.9+9 (x64)", "Eclipse Adoptium", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Rust 1.75 (MSVC 64-bit)", "The Rust Project Developers", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Rust toolchain", "The Rust Project Developers", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Go Programming Language amd64 go1.21.5", "https://go.dev", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Ruby 3.2.2-1-x64", "RubyInstaller", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "PostgreSQL 16", "PostgreSQL Global Development Group", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "MySQL Server 8.0", "Oracle Corporation", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "CMake", "Kitware", C, UninstallRiskReason.DevRuntime),
        P("devruntime", "Strawberry Perl (64-bit)", "Larry Wall", C, UninstallRiskReason.DevRuntime),

        // Manufacturer tools.
        P("oem", "Dell SupportAssist", "Dell Inc.", C, UninstallRiskReason.OemTool),
        P("oem", "HP Smart", "HP Inc.", C, UninstallRiskReason.OemTool),
        P("oem", "Lenovo Vantage Service", "Lenovo Group Limited", C, UninstallRiskReason.OemTool),
        P("oem", "Armoury Crate Service", "ASUSTeK COMPUTER INC.", C, UninstallRiskReason.OemTool),
        P("oem", "System Control Manager", "Micro-Star International Co., Ltd.", C, UninstallRiskReason.OemTool),
        P("oem", "GIGABYTE Control Center", "GIGA-BYTE Technology Co., Ltd.", C, UninstallRiskReason.OemTool),
        P("oem", "Support Assistant", "Hewlett-Packard Company", C, UninstallRiskReason.OemTool),
        P("oem", "Customer Participation", "HP Development Company, L.P.", C, UninstallRiskReason.OemTool),
        P("oem", "Logitech G HUB", "Logitech", C, UninstallRiskReason.OemTool),
        P("oem", "Razer Synapse 3", "Razer Inc.", C, UninstallRiskReason.OemTool),
        P("oem", "Samsung Magician", "Samsung Electronics Co., Ltd.", C, UninstallRiskReason.OemTool),
        P("oem", "iCUE 4 Software", "Corsair Memory, Inc.", C, UninstallRiskReason.OemTool),
        P("oem", "WD Discovery", "Western Digital Corporation", C, UninstallRiskReason.OemTool),
        P("oem", "Helper", "Dell Inc.", C, UninstallRiskReason.OemTool),
        Store("oem", "E046963F.LenovoSettingsEngine", "E046963F.LenovoSettingsEngine_k1h2ywk1493x8", "CN=Zzz", C, UninstallRiskReason.OemTool),
        Store("oem", "AD2F1837.HPSmart", "AD2F1837.HPSmart_v10z8vjag6ke6", "CN=Zzz", C, UninstallRiskReason.OemTool),

        // Generic runtime words.
        P("runtime-generic", "Apple Application Support (64-bit)", "Apple Inc.", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "Bonjour", "Apple Inc.", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "OpenAL", "Acme Widgets Ltd", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "K-Lite Codec Pack 17.8.0 Full", "Codec Guide", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "MATLAB Runtime 9.13", "MathWorks", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "Ghostscript 10.02", "Artifex Software Inc.", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "Adobe AIR", "Adobe Inc.", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "Acme UI Framework", "Acme Widgets Ltd", C, UninstallRiskReason.Runtime),
        P("runtime-generic", "MySQL Connector/ODBC 8.0", "Oracle Corporation", C, UninstallRiskReason.Runtime),

        // Own uninstaller window, unknown publisher, incomplete facts.
        new("interactive", ProgramInput("Acme Legacy Suite", "Acme Widgets Ltd", method: UninstallMethod.Interactive), C, UninstallRiskReason.Interactive),
        P("unknown", "Acme Legacy Suite", string.Empty, C, UninstallRiskReason.Unknown),
        P("unknown", "Acme Legacy Suite", "   ", C, UninstallRiskReason.Unknown),
        P("unknown", "Acme Legacy Suite", null, C, UninstallRiskReason.Unknown),
        new("unknown", ProgramInput("Acme Legacy Suite", "Acme Widgets Ltd") with { InstallLocation = @"C:\Windows\Installer\Acme" }, C, UninstallRiskReason.Unknown),
        new("incomplete", ProgramInput("Acme Legacy Suite", "Acme Widgets Ltd", method: null), C, UninstallRiskReason.Incomplete),
        new("incomplete", ProgramInput("Acme Legacy Suite", string.Empty, UninstallScope.User), C, UninstallRiskReason.Incomplete),
        new("incomplete", ProgramInput(string.Empty, "Acme Widgets Ltd"), C, UninstallRiskReason.Incomplete),
        new("incomplete", ProgramInput("   ", "Acme Widgets Ltd"), C, UninstallRiskReason.Incomplete),
        new("incomplete", ProgramInput("Acme Legacy Suite", null, UninstallScope.User), C, UninstallRiskReason.Incomplete),
        new("incomplete", new UninstallRiskInput { Kind = UninstallEntryKind.Package, DisplayName = "tool", RequiredByOthers = false }, C, UninstallRiskReason.Incomplete),

        // Other Store apps.
        Store("storedata", "4DF9E0F8.Netflix", "4DF9E0F8.Netflix_mcm4njqhnhss8", "CN=Netflix, Inc.", C, UninstallRiskReason.StoreData),
        Store("storedata", "SpotifyAB.SpotifyMusic", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "CN=Spotify AB", C, UninstallRiskReason.StoreData),
        Store("storedata", "Clipchamp.Clipchamp", "Clipchamp.Clipchamp_yxz26nhyzhsrt", "CN=Clipchamp", C, UninstallRiskReason.StoreData),
        Store("storedata", "Contoso.Thing", "Contoso.Thing_abc123", "CN=Contoso", C, UninstallRiskReason.StoreData),

        // Packages: dependents, the id table.
        Pkg("needed", "pip", "requests", "requests", true, C, UninstallRiskReason.NeededByOthers),
        Pkg("needed", "npm", "lodash", "lodash", true, C, UninstallRiskReason.NeededByOthers),
        Pkg("incomplete", "pip", "flask", "flask", null, C, UninstallRiskReason.Incomplete),
        Pkg("pm-itself", "pip", "pip", "pip", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "pip", "setuptools", "setuptools", true, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "npm", "npm", "npm", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "npm", "corepack", "corepack", null, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "pnpm", "pnpm", "pnpm", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "pnpm", "yarn", "yarn", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "chocolatey", "chocolatey", "chocolatey", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "scoop", "7zip", "7zip", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "scoop", "git", "git", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "psgallery", "PowerShellGet", "PowerShellGet", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "psgallery", "Microsoft.WinGet.Client", "Microsoft.WinGet.Client", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "gcloud", "core", "Cloud SDK Core Libraries", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("pm-itself", "gem", "bundler", "bundler", false, R, UninstallRiskReason.PackageManagerItself),
        Pkg("runtime", "chocolatey", "vcredist140", "vcredist140", false, R, UninstallRiskReason.Runtime),
        Pkg("runtime", "chocolatey", "dotnet-8.0-runtime", "dotnet-8.0-runtime", false, R, UninstallRiskReason.Runtime),
        Pkg("runtime", "chocolatey", "netfx-4.8", "netfx-4.8", false, R, UninstallRiskReason.Runtime),
        Pkg("runtime", "chocolatey", "webview2-runtime", "webview2-runtime", false, R, UninstallRiskReason.Runtime),
        Pkg("update", "chocolatey", "kb5001567", "kb5001567", false, R, UninstallRiskReason.Update),
        Pkg("devruntime", "chocolatey", "python3", "python3", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "chocolatey", "git", "git", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "chocolatey", "nodejs-lts", "nodejs-lts", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "scoop", "python", "python", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "mise", "node@20.1.0", "node (mise)", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "volta", "volta-node", "node (Volta)", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "pyenv-win", "pyenv-win-3.12.1", "Python 3.12.1 (pyenv)", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "android-sdk", "platform-tools", "Android SDK Platform-Tools", false, C, UninstallRiskReason.DevRuntime),
        Pkg("devruntime", "android-sdk", "build-tools;34.0.0", "Android SDK Build-Tools 34", false, C, UninstallRiskReason.DevRuntime),

        // Driver and Windows update entries.
        new("driver", new UninstallRiskInput { Kind = UninstallEntryKind.Driver, DisplayName = "Foo Adapter" }, R, UninstallRiskReason.Driver),
        new("update", new UninstallRiskInput { Kind = UninstallEntryKind.WindowsUpdate, DisplayName = "Update for Windows (KB5001567)" }, R, UninstallRiskReason.Update),

        // Ordinary programs and packages.
        P("ordinary", "7-Zip 23.01 (x64)", "Igor Pavlov", S, UninstallRiskReason.Ordinary),
        P("ordinary", "VLC media player", "VideoLAN", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Notepad++ (64-bit x64)", "Notepad++ Team", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Paint.NET", "dotPDN LLC", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Audacity 3.4.2", "Audacity Team", S, UninstallRiskReason.Ordinary),
        P("ordinary", "GIMP 2.10.36", "The GIMP Team", S, UninstallRiskReason.Ordinary),
        P("ordinary", "WinRAR 6.24 (64-bit)", "win.rar GmbH", S, UninstallRiskReason.Ordinary),
        P("ordinary", "FileZilla 3.66.4", "Tim Kosse", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Zoom Workplace (64-bit)", "Zoom Video Communications, Inc.", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Mozilla Firefox (x64 en-US)", "Mozilla", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Google Chrome", "Google LLC", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Blender", "Blender Foundation", S, UninstallRiskReason.Ordinary),
        P("ordinary", "Wireshark 4.2.0 x64", "The Wireshark developer community", S, UninstallRiskReason.Ordinary),
        PU("ordinary", "Spotify", "Spotify AB", S, UninstallRiskReason.Ordinary),
        PU("ordinary", "Slack", "Slack Technologies Inc.", S, UninstallRiskReason.Ordinary),
        PU("ordinary", "Acme Legacy Suite", string.Empty.Length == 0 ? "Acme Widgets Ltd" : null, S, UninstallRiskReason.Ordinary),
        new("ordinary", ProgramInput("Acme Own Window Tool", "Acme Widgets Ltd", UninstallScope.User, UninstallMethod.Interactive), S, UninstallRiskReason.Ordinary),
        Pkg("ordinary", "chocolatey", "googlechrome", "googlechrome", false, S, UninstallRiskReason.Ordinary),
        Pkg("ordinary", "scoop", "ripgrep", "ripgrep", false, S, UninstallRiskReason.Ordinary),
        Pkg("ordinary", "pip", "black", "black", false, S, UninstallRiskReason.Ordinary),
        Pkg("ordinary", "npm", "typescript", "typescript", false, S, UninstallRiskReason.Ordinary),
        Pkg("ordinary", "psgallery", "PSReadLine", "PSReadLine", false, S, UninstallRiskReason.Ordinary),

        // Decoys: names that contain a rule word without being the thing.
        P("decoy", "IntelliJ IDEA Community Edition 2023.3", "JetBrains s.r.o.", S, UninstallRiskReason.Ordinary),
        P("decoy", "Intelligent Search 2", "IntelliCorp", S, UninstallRiskReason.Ordinary),
        P("decoy", "Netflix", "Netflix, Inc.", S, UninstallRiskReason.Ordinary),
        P("decoy", "Amdocs CRM Client", "Amdocs Ltd", S, UninstallRiskReason.Ordinary),
        P("decoy", "Acme amd64 Toolkit", "Acme Widgets Ltd", S, UninstallRiskReason.Ordinary),
        P("decoy", "Avastar Designer", "Avastar Studio", S, UninstallRiskReason.Ordinary),
        P("decoy", "Pythonista", "Omz Software", S, UninstallRiskReason.Ordinary),
        P("decoy", "Gitter Desktop", "Gitter Ltd", S, UninstallRiskReason.Ordinary),
        P("decoy", "Monty Python Quotes", "Quote Company", S, UninstallRiskReason.Ordinary),
        P("decoy", "Rust", "Facepunch Studios", S, UninstallRiskReason.Ordinary),
        P("decoy", "JavaScript Playground", "Acme Widgets Ltd", S, UninstallRiskReason.Ordinary),
        P("decoy", "Dartagnan Reader", "Acme Widgets Ltd", S, UninstallRiskReason.Ordinary),
        P("decoy", "Dellwood Notes", "Dellwood Press", S, UninstallRiskReason.Ordinary),
        P("decoy", "Steamworks Helper", "Acme Widgets Ltd", S, UninstallRiskReason.Ordinary),
        P("decoy", "Drivetrain Planner", "Acme Widgets Ltd", S, UninstallRiskReason.Ordinary),
    ];

    [Fact]
    public void The_fixture_covers_enough_entries()
    {
        Assert.True(Fixture.Length >= 120, $"only {Fixture.Length} fixture entries");
    }

    [Fact]
    public void Every_fixture_entry_gets_its_hand_set_level_and_reason()
    {
        var wrong = new List<string>();
        foreach (var entry in Fixture)
        {
            var risk = UninstallRiskClassifier.Classify(entry.Input);
            if (risk.Level != entry.Level || risk.Reason != entry.Reason)
                wrong.Add($"{entry.Input.DisplayName} [{entry.Category}]: expected {entry.Level}/{entry.Reason}, got {risk.Level}/{risk.Reason}");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Every_category_keeps_at_least_its_minimum_level()
    {
        var wrong = new List<string>();
        foreach (var entry in Fixture)
        {
            if (!MinimumLevelOf.TryGetValue(entry.Category, out var minimum))
            {
                wrong.Add($"category without a minimum: {entry.Category}");
                continue;
            }

            var risk = UninstallRiskClassifier.Classify(entry.Input);
            if (risk.Level < minimum)
                wrong.Add($"{entry.Input.DisplayName} [{entry.Category}]: {risk.Level} is below {minimum}");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void A_missing_fact_never_leaves_an_ordinary_program_safe()
    {
        foreach (var entry in Fixture.Where(e => e.Category == "ordinary" && e.Input.Kind == UninstallEntryKind.Program))
        {
            foreach (var cut in new[]
            {
                entry.Input with { DisplayName = null },
                entry.Input with { Publisher = null },
                entry.Input with { Publisher = "  " },
                entry.Input with { Method = null },
            })
            {
                Assert.NotEqual(CleanupRiskLevel.Safe, UninstallRiskClassifier.Classify(cut).Level);
            }
        }
    }

    [Fact]
    public void A_package_without_a_known_dependent_state_is_never_safe()
    {
        foreach (var entry in Fixture.Where(e => e.Category == "ordinary" && e.Input.Kind == UninstallEntryKind.Package))
        {
            Assert.NotEqual(CleanupRiskLevel.Safe, UninstallRiskClassifier.Classify(entry.Input with { RequiredByOthers = null }).Level);
            Assert.NotEqual(CleanupRiskLevel.Safe, UninstallRiskClassifier.Classify(entry.Input with { SourceId = null }).Level);
            Assert.NotEqual(CleanupRiskLevel.Safe, UninstallRiskClassifier.Classify(entry.Input with { PackageId = " " }).Level);
        }
    }

    [Fact]
    public void A_vendor_match_beats_a_missing_publisher_and_a_missing_method()
    {
        var input = ProgramInput("Realtek Audio Driver", null, method: null);

        Assert.Equal(CleanupRiskLevel.Risky, UninstallRiskClassifier.Classify(input).Level);
    }

    // ---- Rule 1 and the Method result.

    [Theory]
    [InlineData(@"{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1")]
    [InlineData(@"{8c4f2a91-3b7d-4e5a-9c21-6d8e4f1b2a73}_IS1")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1")]
    [InlineData(@"  {8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1  ")]
    public void The_own_entry_is_considered_and_cannot_be_ticked(string keyName)
    {
        foreach (var scope in new[] { UninstallScope.Machine, UninstallScope.User })
        {
            var risk = UninstallRiskClassifier.Classify(ProgramInput("Ultimate Updater", "BGCoding", scope) with { UninstallKeyName = keyName });

            Assert.Equal(new UninstallRisk(CleanupRiskLevel.Considered, UninstallRiskReason.Own, false), risk);
        }
    }

    [Theory]
    [InlineData(@"{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}")]
    [InlineData(@"{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is2")]
    [InlineData(@"x{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1")]
    [InlineData(@"{00000000-0000-0000-0000-000000000000}_is1")]
    public void A_key_that_only_looks_like_the_own_one_is_rated_as_any_other_program(string keyName)
    {
        var risk = UninstallRiskClassifier.Classify(ProgramInput("Acme Tool", "Acme Widgets Ltd") with { UninstallKeyName = keyName });

        Assert.Equal(UninstallRiskReason.Ordinary, risk.Reason);
        Assert.True(risk.Selectable);
    }

    [Fact]
    public void The_own_entry_wins_over_every_other_rule()
    {
        var risk = UninstallRiskClassifier.Classify(new UninstallRiskInput
        {
            Kind = UninstallEntryKind.Driver,
            DisplayName = "Bitdefender",
            UninstallKeyName = UninstallRiskClassifier.OwnUninstallKeyName,
        });

        Assert.Equal(UninstallRiskReason.Own, risk.Reason);
        Assert.False(risk.Selectable);
    }

    [Theory]
    [InlineData("Acme Tool", "Acme Widgets Ltd")]
    [InlineData("Bitdefender Total Security", "Bitdefender")]
    [InlineData("Realtek Audio Driver", "Realtek")]
    public void A_refused_removal_keeps_its_level_but_cannot_be_ticked(string name, string publisher)
    {
        var normal = UninstallRiskClassifier.Classify(ProgramInput(name, publisher));
        var refused = UninstallRiskClassifier.Classify(ProgramInput(name, publisher, method: UninstallMethod.Refused));

        Assert.True(normal.Selectable);
        Assert.False(refused.Selectable);
        Assert.Equal(normal.Level, refused.Level);
        Assert.Equal(normal.Reason, refused.Reason);
    }

    // ---- Rule order.

    [Fact]
    public void Edge_channels_stay_considered_while_Edge_itself_is_risky()
    {
        Assert.Equal(CleanupRiskLevel.Considered, UninstallRiskClassifier.Classify(ProgramInput("Microsoft Edge Dev", "Microsoft Corporation")).Level);
        Assert.Equal(CleanupRiskLevel.Risky, UninstallRiskClassifier.Classify(ProgramInput("Microsoft Edge", "Microsoft Corporation")).Level);
        Assert.Equal(CleanupRiskLevel.Risky, UninstallRiskClassifier.Classify(ProgramInput("Microsoft Edge Update", "Microsoft Corporation")).Level);
    }

    [Fact]
    public void A_driver_updater_stays_considered_although_its_name_says_driver()
    {
        var risk = UninstallRiskClassifier.Classify(ProgramInput("DriverPack Solution", "Artur Kuzyakov"));

        Assert.Equal(CleanupRiskLevel.Considered, risk.Level);
        Assert.Equal(UninstallRiskReason.OemTool, risk.Reason);
    }

    [Fact]
    public void A_vendor_brand_in_the_name_counts_although_an_oem_published_it()
    {
        Assert.Equal(UninstallRiskReason.Hardware, UninstallRiskClassifier.Classify(ProgramInput("Realtek Audio Console", "Dell Inc.")).Reason);
        Assert.Equal(UninstallRiskReason.Hardware, UninstallRiskClassifier.Classify(ProgramInput("Intel Management Engine Components", "HP Inc.")).Reason);
    }

    [Fact]
    public void Security_wins_over_the_publisher_being_Microsoft()
    {
        Assert.Equal(UninstallRiskReason.Security, UninstallRiskClassifier.Classify(ProgramInput("Microsoft Defender for Endpoint", "Microsoft Corporation")).Reason);
    }

    [Fact]
    public void A_virtualization_product_is_not_taken_for_a_hardware_vendor_but_its_guest_tools_are_hardware()
    {
        Assert.Equal(UninstallRiskReason.Virtualization, UninstallRiskClassifier.Classify(ProgramInput("VMware Workstation 17 Pro", "Broadcom Inc.")).Reason);
        Assert.Equal(UninstallRiskReason.Hardware, UninstallRiskClassifier.Classify(ProgramInput("VMware Tools", "Broadcom Inc.")).Reason);
        Assert.Equal(UninstallRiskReason.Hardware, UninstallRiskClassifier.Classify(ProgramInput("Parallels Tools", "Parallels International GmbH")).Reason);
    }

    [Fact]
    public void A_machine_program_in_the_Windows_folder_without_a_known_vendor_is_unknown()
    {
        var input = ProgramInput("Acme Legacy Suite", "Acme Widgets Ltd");

        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Windows\System32\Acme" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"c:\WINDOWS" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Windows\" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"""C:\Windows\Acme""" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Program Files\..\Windows\Acme" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"\\?\C:\Windows\Acme" }).Reason);
    }

    [Fact]
    public void An_install_location_that_cannot_be_judged_raises_instead_of_passing()
    {
        var input = ProgramInput("Acme Legacy Suite", "Acme Widgets Ltd");

        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"Acme\Bin" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"%ProgramFiles%\Acme" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\WINDOW~1\Acme" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = "C:\\Acme\0\\Bin" }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Acme", WindowsDirectory = null }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Acme", WindowsDirectory = " " }).Reason);
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Acme", WindowsDirectory = @"%SystemRoot%" }).Reason);
    }

    [Fact]
    public void A_location_outside_the_Windows_folder_and_no_location_at_all_change_nothing()
    {
        var input = ProgramInput("Acme Legacy Suite", "Acme Widgets Ltd");

        Assert.Equal(UninstallRiskReason.Ordinary, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Program Files\Acme" }).Reason);
        Assert.Equal(UninstallRiskReason.Ordinary, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\Windows2\Acme" }).Reason);
        Assert.Equal(UninstallRiskReason.Ordinary, UninstallRiskClassifier.Classify(input with { InstallLocation = @"C:\WindowsApps" }).Reason);
        Assert.Equal(UninstallRiskReason.Ordinary, UninstallRiskClassifier.Classify(input with { InstallLocation = null }).Reason);
        Assert.Equal(UninstallRiskReason.Ordinary, UninstallRiskClassifier.Classify(input with { InstallLocation = "  " }).Reason);
    }

    [Fact]
    public void An_unknown_publisher_only_raises_machine_wide_programs()
    {
        Assert.Equal(UninstallRiskReason.Unknown, UninstallRiskClassifier.Classify(ProgramInput("Acme Legacy Suite", null)).Reason);
        Assert.Equal(UninstallRiskReason.Incomplete, UninstallRiskClassifier.Classify(ProgramInput("Acme Legacy Suite", null, UninstallScope.User)).Reason);
    }

    // ---- Normalization.

    [Theory]
    [InlineData("Intel\u00AE Driver", "intel driver")]
    [InlineData("Foo\u2122 Bar", "foo bar")]
    [InlineData("Foo\u00A9 Bar", "foo bar")]
    [InlineData("Intel(R) Wireless", "intel wireless")]
    [InlineData("Foo(TM) Bar", "foo bar")]
    [InlineData("Foo(c) Bar", "foo bar")]
    [InlineData("Foo\u00A0Bar", "foo bar")]
    [InlineData("  Foo   Bar \t baz \r\n", "foo bar baz")]
    [InlineData("FOO bar", "foo bar")]
    [InlineData("\uFF21\uFF2D\uFF24 Software", "amd software")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Names_are_normalized_before_matching(string input, string expected)
    {
        Assert.Equal(expected, UninstallRiskClassifier.NormalizeName(input));
    }

    [Fact]
    public void An_unpaired_surrogate_in_a_registry_value_does_not_break_the_rating()
    {
        var broken = "Foo\uD800Bar \uDC00 Bitdefender";

        Assert.Equal("foo bar bitdefender", UninstallRiskClassifier.NormalizeName(broken).Replace("  ", " "));
        Assert.Equal(UninstallRiskReason.Security, UninstallRiskClassifier.Classify(ProgramInput(broken, "Acme Widgets Ltd")).Reason);
        Assert.Equal("grin " + char.ConvertFromUtf32(0x1F600), UninstallRiskClassifier.NormalizeName("Grin " + char.ConvertFromUtf32(0x1F600)));
    }

    [Fact]
    public void A_missing_name_normalizes_to_nothing()
    {
        Assert.Equal(string.Empty, UninstallRiskClassifier.NormalizeName(null));
    }

    [Theory]
    [InlineData("Realtek Semiconductor Corp.", "realtek")]
    [InlineData("Microsoft Corporation", "microsoft")]
    [InlineData("Gigabyte Technology Co., Ltd.", "gigabyte")]
    [InlineData("Hewlett-Packard Company", "hewlett packard")]
    [InlineData("HP Development Company, L.P.", "hp development")]
    [InlineData("Dell Technologies Inc.", "dell")]
    [InlineData("M\u00FCller & Co. KG", "m\u00FCller")]
    [InlineData("Foo A/S", "foo")]
    [InlineData("Foo spol. s r.o.", "foo")]
    [InlineData("Foo s.r.o.", "foo")]
    [InlineData("Foo B.V.", "foo")]
    [InlineData("Foo e.U.", "foo")]
    [InlineData("Foo Systems International Software Operations", "foo")]
    [InlineData("Systems", "systems")]
    [InlineData("Inc.", "inc")]
    [InlineData("", "")]
    public void Publishers_lose_their_trailing_legal_forms_and_always_keep_one_word(string publisher, string expected)
    {
        Assert.Equal(expected, UninstallRiskClassifier.NormalizePublisher(publisher));
    }

    [Theory]
    [InlineData("Realtek Semiconductor Corp.", "Realtek", true)]
    [InlineData("Intel Corporation", "Intel", true)]
    [InlineData("IntelliJ s.r.o.", "Intel", false)]
    [InlineData("Intellicorp", "Intel", false)]
    [InlineData("Intel", "Intel Corporation", true)]
    [InlineData("Micro-Star International Co., Ltd.", "Micro-Star", true)]
    [InlineData("Micro-Star International Co., Ltd.", "MSI", false)]
    [InlineData("Advanced Micro Devices, Inc.", "AMD", false)]
    [InlineData("Advanced Micro Devices, Inc.", "Advanced Micro Devices", true)]
    [InlineData("Not Intel", "Intel", false)]
    [InlineData("", "Intel", false)]
    [InlineData(null, "Intel", false)]
    [InlineData("Intel", "", false)]
    public void A_publisher_matches_a_vendor_by_whole_leading_words(string? publisher, string vendor, bool expected)
    {
        Assert.Equal(expected, UninstallRiskClassifier.PublisherMatchesVendor(publisher, vendor));
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("CN=Foo", "Foo")]
    [InlineData("O=Bar, CN=Foo", "Bar")]
    [InlineData("CN=\"Foo, Inc.\", O=\"Foo, Inc.\", C=US", "Foo, Inc.")]
    [InlineData("CN=Foo, O=", "Foo")]
    [InlineData("Plain Name", "Plain Name")]
    [InlineData("", "")]
    public void A_Store_publisher_is_the_organization_of_the_certificate_else_its_common_name(string subject, string expected)
    {
        Assert.Equal(expected, UninstallRiskClassifier.StorePublisher(subject));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe", true)]
    [InlineData("MSTeams_CW5N1H2TXYEWY", true)]
    [InlineData("Contoso.Thing_abc123", false)]
    [InlineData("Microsoft.Fake_8wekyb3d8bbwe_extra", false)]
    [InlineData("Microsoft.Fake8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.Fake_", false)]
    [InlineData("", false)]
    public void A_Microsoft_Store_app_is_known_by_the_publisher_id_of_its_family(string family, bool expected)
    {
        Assert.Equal(expected, UninstallRiskClassifier.HasMicrosoftPublisherId(family));
    }

    [Fact]
    public void A_Store_app_with_a_foreign_family_is_not_Microsoft_whatever_its_certificate_says()
    {
        var risk = UninstallRiskClassifier.Classify(new UninstallRiskInput
        {
            Kind = UninstallEntryKind.StoreApp,
            DisplayName = "Contoso.Thing",
            PackageFamilyName = "Contoso.Thing_abc123",
            Publisher = MicrosoftCertificate,
            Method = UninstallMethod.StoreApp,
        });

        Assert.Equal(UninstallRiskReason.StoreData, risk.Reason);
    }

    // ---- Hostile input.

    [Fact]
    public void Hostile_input_never_throws_and_keeps_the_invariants()
    {
        var pieces = new[]
        {
            null, string.Empty, " ", "\0", "\r\n", "(((((", "[", "\\", "(?<", "*+?", "%PATH%", "..\\..", "C:\\Windows",
            "intel", "Intel", "AMD", "driver", "Python 3", "Rust (MSVC", "Microsoft", "Microsoft Corporation", "O=", "CN=\"",
            "{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1", "Microsoft.Foo_8wekyb3d8bbwe", "AppUp.", "\u00AE", "\u2122",
            "\u00A0", "\uFF21", "\uFFFF", char.ConvertFromUtf32(0x1F600), "\uD800", new string('a', 5000), string.Concat(Enumerable.Repeat("intel ", 2000)),
            "pip", "npm", "kb123", "volta-node", "node@", "@", "/", "build-tools;1",
        };
        var kinds = Enum.GetValues<UninstallEntryKind>();
        var scopes = Enum.GetValues<UninstallScope>();
        var methods = new UninstallMethod?[] { null }.Concat(Enum.GetValues<UninstallMethod>().Select(m => (UninstallMethod?)m)).ToArray();
        var flags = new bool?[] { null, true, false };
        var random = new Random(718);

        for (var i = 0; i < 6000; i++)
        {
            var input = new UninstallRiskInput
            {
                Kind = kinds[random.Next(kinds.Length)],
                DisplayName = Pick(pieces, random),
                Publisher = Pick(pieces, random),
                PackageFamilyName = Pick(pieces, random),
                SourceId = Pick(pieces, random),
                PackageId = Pick(pieces, random),
                Scope = scopes[random.Next(scopes.Length)],
                Method = methods[random.Next(methods.Length)],
                UninstallKeyName = Pick(pieces, random),
                InstallLocation = Pick(pieces, random),
                WindowsDirectory = Pick(pieces, random),
                RequiredByOthers = flags[random.Next(flags.Length)],
            };

            var risk = UninstallRiskClassifier.Classify(input);

            if (input.Method == UninstallMethod.Refused)
                Assert.False(risk.Selectable);

            if (risk.Reason == UninstallRiskReason.Own)
                Assert.False(risk.Selectable);

            if (risk.Level == CleanupRiskLevel.Safe)
            {
                Assert.Equal(UninstallRiskReason.Ordinary, risk.Reason);
                Assert.True(HasCompleteFacts(input), $"Safe with missing facts: {input}");
            }

            if (input.Kind is UninstallEntryKind.Driver or UninstallEntryKind.WindowsUpdate && risk.Reason != UninstallRiskReason.Own)
                Assert.Equal(CleanupRiskLevel.Risky, risk.Level);
        }
    }

    private static string? Pick(string?[] pieces, Random random) =>
        random.Next(3) == 0 ? pieces[random.Next(pieces.Length)] + pieces[random.Next(pieces.Length)] : pieces[random.Next(pieces.Length)];

    private static bool HasCompleteFacts(UninstallRiskInput input)
    {
        if (string.IsNullOrWhiteSpace(input.DisplayName))
            return false;

        return input.Kind switch
        {
            UninstallEntryKind.Program => !string.IsNullOrWhiteSpace(input.Publisher) && input.Method is not null,
            UninstallEntryKind.StoreApp => !string.IsNullOrWhiteSpace(input.PackageFamilyName),
            UninstallEntryKind.Package => !string.IsNullOrWhiteSpace(input.SourceId) && !string.IsNullOrWhiteSpace(input.PackageId) && input.RequiredByOthers is not null,
            _ => false,
        };
    }

    [Fact]
    public void A_huge_name_is_rated_quickly()
    {
        var stopwatch = Stopwatch.StartNew();
        var name = string.Concat(Enumerable.Repeat("a(b)[c]x ", 40_000));

        var risk = UninstallRiskClassifier.Classify(ProgramInput(name, "Acme Widgets Ltd"));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        Assert.Equal(UninstallRiskReason.Ordinary, risk.Reason);
    }

    [Fact]
    public void A_vendor_word_beyond_the_name_length_cap_is_not_looked_at()
    {
        var name = new string('x', 1100) + " Bitdefender";

        Assert.Equal(1024, UninstallRiskClassifier.NormalizeName(name).Length);
        Assert.Equal(UninstallRiskReason.Ordinary, UninstallRiskClassifier.Classify(ProgramInput(name, "Acme Widgets Ltd")).Reason);
    }

    [Fact]
    public void Rating_the_same_entry_twice_gives_the_same_answer()
    {
        foreach (var entry in Fixture)
            Assert.Equal(UninstallRiskClassifier.Classify(entry.Input), UninstallRiskClassifier.Classify(entry.Input));
    }

    [Fact]
    public void A_null_input_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => UninstallRiskClassifier.Classify(null!));
    }

    // ---- Texts.

    [Fact]
    public void Every_reason_has_a_text_in_every_language()
    {
        var resources = FindResourcesDirectory();
        var files = Directory.GetFiles(resources, "Strings*.resx");
        Assert.True(files.Length >= 12, "expected the neutral file and eleven locales");

        foreach (var file in files)
        {
            var keys = XDocument.Load(file).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();
            foreach (var reason in Enum.GetValues<UninstallRiskReason>())
            {
                var key = new UninstallRisk(CleanupRiskLevel.Safe, reason, true).ReasonKey;
                Assert.True(keys.Contains(key), $"{Path.GetFileName(file)} lacks {key}");
            }
        }
    }

    [Fact]
    public void The_reasons_are_the_twenty_four_of_the_specification()
    {
        Assert.Equal(24, Enum.GetValues<UninstallRiskReason>().Length);
    }

    private static string FindResourcesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Resources");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Resources directory from the test output path.");
    }
}
