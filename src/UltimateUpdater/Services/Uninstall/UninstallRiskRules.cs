using System.Text.RegularExpressions;
using UltimateUpdater.Services.Cleanup;
using Subject = UltimateUpdater.Services.Uninstall.UninstallRiskClassifier.Subject;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The ordered rule tables of <see cref="UninstallRiskClassifier"/> (docs/DESIGN.md, "Uninstaller risk rating"). A table
/// is a block of lines, one entry each:
/// <list type="bullet">
/// <item>plain text: a name pattern, matched on whole words ("amd" never matches "amd64"); "|" separates alternatives;</item>
/// <item>"v:" a vendor: its text as a name pattern AND as a publisher alias (publisher token prefix);</item>
/// <item>"p:" a publisher alias only (for vendor names too common as a word);</item>
/// <item>"s:" a Store package family prefix;</item>
/// <item>"re:" a regular expression with a leading word boundary, for names a plain pattern cannot say.</item>
/// </list>
/// Every name pattern is normalized like the name it is matched against. A pattern match that runs into the time limit
/// counts as a match: doubt raises the level.
/// </summary>
internal static class UninstallRiskRules
{
    /// <summary>The trailing legal-form words of a publisher, longest first; "L.P." and "A/S" are the token runs "l p" and "a s".</summary>
    private static readonly string[][] LegalForms = (
        "inc|incorporated|incorporation|corp|corporation|company|co|ltd|limited|llc|l p|lp|plc|group|gmbh|ag|kg|co kg|ab|oy|as|a s|aps"
        + "|s r o|spol s r o|s a|sa|sas|b v|bv|n v|nv|pty|e u|technologies|technology|semiconductor|electronics|computer"
        + "|laboratories|systems|international|microelectronics|software|operations")
        .Split('|')
        .Select(form => form.Split(' '))
        .OrderByDescending(form => form.Length)
        .ToArray();

    /// <summary>The leading tokens of a publisher equal the vendor's tokens, never a text prefix.</summary>
    internal static bool HasTokenPrefix(string[] tokens, string[] vendorTokens)
    {
        if (vendorTokens.Length == 0 || tokens.Length < vendorTokens.Length)
            return false;

        for (var i = 0; i < vendorTokens.Length; i++)
        {
            if (!string.Equals(tokens[i], vendorTokens[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    internal static string[] VendorTokens(string vendor) =>
        StripLegalForms(UninstallRiskClassifier.Tokens(UninstallRiskClassifier.NormalizeName(vendor)));

    /// <summary>Strips trailing legal forms repeatedly; one token always stays.</summary>
    internal static string[] StripLegalForms(string[] tokens)
    {
        var end = tokens.Length;
        while (end > 1)
        {
            var removed = false;
            foreach (var form in LegalForms)
            {
                if (form.Length < end && EndsWith(tokens, end, form))
                {
                    end -= form.Length;
                    removed = true;
                    break;
                }
            }

            if (!removed)
                break;
        }

        return end == tokens.Length ? tokens : tokens[..end];
    }

    private static bool EndsWith(string[] tokens, int end, string[] form)
    {
        for (var i = 0; i < form.Length; i++)
        {
            if (!string.Equals(tokens[end - form.Length + i], form[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private enum Applies
    {
        Any,
        StoreOnly,
        NotStore,
    }

    /// <summary>One table: whole-word name patterns, regular expressions, publisher aliases and Store family prefixes.</summary>
    private sealed class Group
    {
        private readonly Regex? names;
        private readonly Regex[] patterns;
        private readonly string[][] publishers;
        private readonly string[] families;
        private readonly Applies applies;

        public Group(string entries, Applies applies = Applies.Any)
        {
            this.applies = applies;
            var nameParts = new List<string>();
            var regexes = new List<Regex>();
            var aliases = new List<string[]>();
            var prefixes = new List<string>();

            foreach (var raw in entries.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("re:", StringComparison.Ordinal))
                {
                    regexes.Add(new Regex(@"(?<![\p{L}\p{N}])(?:" + line[3..] + ")", RegexOptions.CultureInvariant, UninstallRiskClassifier.MatchTimeout));
                    continue;
                }

                var asVendor = line.StartsWith("v:", StringComparison.Ordinal);
                var asPublisher = line.StartsWith("p:", StringComparison.Ordinal);
                var asFamily = line.StartsWith("s:", StringComparison.Ordinal);
                var body = asVendor || asPublisher || asFamily ? line[2..] : line;

                foreach (var alternative in body.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (asFamily)
                    {
                        prefixes.Add(UninstallRiskClassifier.Lower(alternative.TrimEnd('*')));
                        continue;
                    }

                    var normalized = UninstallRiskClassifier.NormalizeName(alternative);
                    if (normalized.Length == 0)
                        throw new InvalidOperationException($"Empty rule entry: {line}");

                    if (!asPublisher)
                        nameParts.Add(Regex.Escape(normalized));

                    if (asVendor || asPublisher)
                    {
                        var tokens = VendorTokens(alternative);
                        if (tokens.Length == 0)
                            throw new InvalidOperationException($"Rule entry without a word: {line}");

                        aliases.Add(tokens);
                    }
                }
            }

            names = nameParts.Count == 0
                ? null
                : new Regex(@"(?<![\p{L}\p{N}])(?:" + string.Join('|', nameParts) + @")(?![\p{L}\p{N}])", RegexOptions.CultureInvariant, UninstallRiskClassifier.MatchTimeout);
            patterns = [.. regexes];
            publishers = [.. aliases];
            families = [.. prefixes];
        }

        public bool Matches(Subject subject)
        {
            var isStore = subject.Kind == UninstallEntryKind.StoreApp;
            if ((applies == Applies.StoreOnly && !isStore) || (applies == Applies.NotStore && isStore))
                return false;

            if (isStore && families.Length > 0)
            {
                var family = UninstallRiskClassifier.Lower(subject.Family);
                if (families.Any(prefix => family.StartsWith(prefix, StringComparison.Ordinal) || subject.Name.StartsWith(prefix, StringComparison.Ordinal)))
                    return true;
            }

            if (subject.Name.Length > 0 && NameMatches(subject.Name))
                return true;

            return subject.PublisherTokens.Length > 0 && publishers.Any(alias => HasTokenPrefix(subject.PublisherTokens, alias));
        }

        private bool NameMatches(string name)
        {
            try
            {
                return (names?.IsMatch(name) ?? false) || patterns.Any(pattern => pattern.IsMatch(name));
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
        }
    }

    private static UninstallRisk Rated(CleanupRiskLevel level, UninstallRiskReason reason) => new(level, reason, true);

    private static Func<Subject, UninstallRisk?> Step(Group group, CleanupRiskLevel level, UninstallRiskReason reason)
    {
        var risk = Rated(level, reason);
        return subject => group.Matches(subject) ? risk : null;
    }

    // ---- 5. Considered exceptions, checked before every Risky name rule.

    private static readonly Group DriverUpdaters = new("""
        Driver Booster
        Driver Easy
        DriverPack
        Snappy Driver Installer
        Driver Talent
        """);

    private static readonly Group Bundleware = new("""
        McAfee WebAdvisor
        McAfee Security Scan Plus
        Avast Secure Browser
        AVG Secure Browser
        Avast Cleanup
        AVG Cleanup
        AVG TuneUp
        Avast Driver Updater
        Norton Safe Web
        """);

    private static readonly Group VendorHelpers = new("""
        NVIDIA GeForce Experience
        NVIDIA app
        GeForce NOW
        Intel Driver & Support Assistant
        Intel Unison
        Waves Central
        """);

    private static readonly Group EdgeChannels = new("""
        Microsoft Edge Beta
        Microsoft Edge Dev
        Microsoft Edge Canary
        """);

    // ---- 6 to 10.

    private static readonly Group Accessibility = new("""
        NVDA
        v:NV Access
        JAWS
        ZoomText
        p:Freedom Scientific
        p:Vispero
        Dolphin SuperNova
        p:Dolphin Computer Access
        re:dragon (?:naturally|professional|home|premium|legal|medical|anywhere|individual|speech)
        p:Nuance Communications
        v:Tobii Dynavox
        """);

    private static readonly Group DataLoss = new("""
        Steam
        p:Valve
        Docker Desktop
        Rancher Desktop
        BlueStacks
        LDPlayer
        MEmu
        NoxPlayer
        XAMPP
        s:CanonicalGroupLimited.
        s:TheDebianProject.
        s:KaliLinux.
        s:46932SUSE.
        s:WhitewaterFoundryLtd.
        s:OracleAmericaInc.OracleLinux
        s:Microsoft.MinecraftUWP
        s:MicrosoftCorporationII.WindowsSubsystemForLinux
        """);

    private static readonly Group Security = new("""
        Microsoft Defender
        v:Bitdefender
        v:Kaspersky|AO Kaspersky Lab
        v:ESET
        v:Norton|NortonLifeLock|Gen Digital
        v:Avast
        v:AVG
        v:McAfee
        v:Sophos
        v:Malwarebytes
        v:Trend Micro
        v:G DATA
        v:F-Secure
        v:WithSecure
        v:Avira
        v:CrowdStrike
        v:SentinelOne|Sentinel Labs|Sentinel Agent
        v:Symantec
        Broadcom Endpoint
        v:Trellix
        v:Webroot
        v:Panda Security|WatchGuard
        Panda Dome
        v:Comodo|Xcitium
        v:Emsisoft
        v:ZoneAlarm|Check Point
        v:Cylance
        Carbon Black
        Cortex XDR
        v:Huorong
        360 Total Security
        v:Qihoo
        v:TotalAV
        v:K7
        v:Quick Heal
        v:Dr.Web|Doctor Web
        v:AhnLab
        v:VIPRE
        v:Heimdal
        re:acronis cyber protect(?! home)
        HP Wolf Security
        Sure Click
        Sure Sense
        Dell Encryption
        Dell Data Protection
        Dell Threat Defense
        Portmaster
        v:AdGuard
        v:GlassWire
        v:NetLimiter
        VeraCrypt
        BitLocker
        DiskCryptor
        TrueCrypt
        Check Point Full Disk Encryption
        Symantec Endpoint Encryption
        PGP
        v:WinMagic
        SecureDoc
        v:DriveLock
        Boxcryptor
        Cryptomator
        Tresorit
        AxCrypt
        Gpg4win
        SafeNet Authentication Client
        ActivClient
        OpenSC
        Charismathics
        Nexus Personal
        YubiKey Smart Card Minidriver
        Encryption
        Verschlüsselung
        Antivirus
        Anti-Virus
        Internet Security
        Total Security
        Endpoint Protection
        Endpoint Security
        Firewall
        Smart Card
        Minidriver
        """);

    private static readonly Group CompanyAccess = new("""
        Cisco AnyConnect
        Cisco Secure Client
        GlobalProtect
        p:Palo Alto Networks
        FortiClient
        v:Fortinet
        Check Point Endpoint Security VPN
        v:Pulse Secure
        Ivanti Secure Access
        v:Zscaler
        v:Netskope
        SonicWall NetExtender
        Sophos Connect
        Workspace ONE Intelligent Hub
        v:ManageEngine
        v:baramundi
        v:Matrix42
        Empirum
        NinjaOne
        NinjaRMMAgent
        Datto RMM
        CentraStage
        v:N-able
        SolarWinds Windows Agent
        Atera Agent
        Kaseya Agent
        ConnectWise Automate
        ScreenConnect Client
        Qualys Cloud Agent
        Tenable Nessus Agent
        Rapid7 Insight Agent
        Wazuh Agent
        Elastic Agent
        Splunk Universal Forwarder
        v:Absolute
        """);

    private static readonly Group RuntimeRisky = new("""
        re:microsoft visual c\+\+[^\n]{0,80}(?:redistributable|runtime)
        re:(?:microsoft )?\.net (?:core |desktop |windows desktop )?(?:runtime|sdk|host|framework)
        re:(?:microsoft )?\.net(?: core)? (?:\d|apphost|native|targeting)
        Microsoft .NET
        Windows Desktop Runtime
        ASP.NET Core
        DirectX
        Windows App Runtime
        Windows App SDK
        Microsoft Edge WebView2 Runtime
        Microsoft Edge
        Visual Studio Installer
        Windows SDK
        Windows Software Development Kit
        Vulkan Run Time Libraries
        Vulkan Runtime
        """);

    // ---- 11 to 13. The guest tools come first: they are drivers inside a virtual machine, and the Virtualization table
    // (Considered) must not swallow them. Virtualization stays ahead of the other Hardware names because Broadcom owns VMware.

    private static readonly Group GuestTools = new("""
        Guest Additions
        VMware Tools
        Parallels Tools
        """);

    private static readonly Group Virtualization = new("""
        Oracle VM VirtualBox
        Oracle VirtualBox
        VirtualBox
        VMware Workstation
        VMware Player
        Vagrant
        Multipass
        QEMU
        Genymotion
        """);

    private static readonly Group HardwareNames = new("""
        Driver
        Drivers
        Treiber
        Windows Driver Package
        Device Software
        Basic Device
        Chipset
        Firmware
        BIOS
        ASIO
        virtio
        Npcap
        WinPcap
        TAP-Windows
        WinFsp
        Dokan
        ViGEm
        HidHide
        SPTD
        ImDisk
        Virtual Audio
        Virtual Cable
        Voicemeeter
        libusb
        Mobile Device Support
        """);

    private static readonly Group HardwareVendors = new("""
        v:Intel
        v:AMD|Advanced Micro Devices
        v:NVIDIA
        v:Realtek
        v:Synaptics
        v:ELAN
        v:Dolby
        p:Waves
        v:Qualcomm
        v:MediaTek
        v:Broadcom
        v:Rivet Networks|Killer
        v:Conexant
        v:Cirrus Logic
        v:Tobii
        v:Canon
        v:Seiko Epson|Epson
        v:Brother
        v:Kyocera
        v:Xerox
        v:Ricoh
        v:Lexmark
        v:Konica Minolta
        p:Sharp
        v:OKI
        v:PFU|ScanSnap
        v:Plustek
        v:DYMO
        v:Zebra
        v:Wacom
        v:Huion
        v:XP-Pen|Hanvon Ugee
        v:Gaomon
        v:Alps|Alps Alpine
        v:FocalTech
        p:Cypress
        v:Goodix
        v:EgisTec
        v:Fingerprint Cards
        p:Validity
        v:Genesys Logic
        v:Alcor Micro
        v:O2Micro
        v:JMicron
        v:ASMedia
        v:Renesas
        v:VIA Technologies
        v:Marvell
        v:Fresco Logic
        v:DisplayLink
        v:Silicon Labs
        v:FTDI
        v:WCH
        v:Prolific
        v:ASIX
        v:TP-Link
        v:Netgear
        v:D-Link
        v:AVM
        v:Edimax
        v:Fibocom
        v:Quectel
        v:Sierra Wireless
        p:Creative
        Creative Technology
        v:A-Volute|Nahimic
        v:DTS|Xperi
        v:Sound Research
        v:Focusrite
        v:Steinberg
        v:Yamaha
        v:Roland
        v:PreSonus
        v:MOTU
        v:Universal Audio
        v:RME
        v:Native Instruments
        v:Music Tribe|Behringer
        v:Thesycon
        v:Audient
        v:Arturia
        v:Elgato
        v:AVerMedia
        v:Blackmagic Design
        v:Magewell
        p:Meta
        v:Oculus
        p:Facebook Technologies
        v:HTC
        v:Varjo
        v:Pimax
        v:NaturalPoint
        v:Thrustmaster|Guillemot
        v:Fanatec|Endor
        v:REINER SCT
        Toshiba Bluetooth
        s:AppUp.
        s:NVIDIACorp.
        s:AdvancedMicroDevicesInc-2.
        s:RealtekSemiconductorCorp.
        s:DolbyLaboratories.
        s:DTSInc.
        s:SynapticsIncorporated.
        s:ELANMicroelectronicsCorpo.
        s:RivetNetworks.
        s:TobiiAB.
        s:A-Volute.
        """);

    // ---- 14. Microsoft allowlists (Considered), everything else from Microsoft is Risky.

    private static readonly Group StoreAllowlist = new("""
        Photos
        Calculator
        Paint
        Notepad
        Clipchamp
        Solitaire
        News
        Weather
        To Do
        Teams
        Terminal
        Sticky Notes
        Snipping Tool
        Camera
        Sound Recorder
        Phone Link
        Dev Home
        Get Help
        Tips
        Feedback Hub
        Maps
        Media Player
        Outlook
        Copilot
        Power Automate
        Quick Assist
        s:Microsoft.Windows.Photos
        s:Microsoft.WindowsCalculator
        s:Microsoft.Paint
        s:Microsoft.WindowsNotepad
        s:Clipchamp.Clipchamp
        s:Microsoft.MicrosoftSolitaireCollection
        s:Microsoft.BingNews
        s:Microsoft.BingWeather
        s:Microsoft.Todos
        s:MSTeams
        s:MicrosoftTeams
        s:Microsoft.WindowsTerminal
        s:Microsoft.MicrosoftStickyNotes
        s:Microsoft.ScreenSketch
        s:Microsoft.WindowsCamera
        s:Microsoft.WindowsSoundRecorder
        s:Microsoft.YourPhone
        s:Microsoft.Windows.DevHome
        s:Microsoft.GetHelp
        s:Microsoft.Getstarted
        s:Microsoft.WindowsFeedbackHub
        s:Microsoft.WindowsMaps
        s:Microsoft.ZuneMusic
        s:Microsoft.OutlookForWindows
        s:Microsoft.Copilot
        s:Microsoft.PowerAutomateDesktop
        s:MicrosoftCorporationII.QuickAssist
        """, Applies.StoreOnly);

    private static readonly Group StorePowerShell = new("s:Microsoft.PowerShell", Applies.StoreOnly);

    private static readonly Group MicrosoftAllowlist = new("""
        Visual Studio Code
        PowerToys
        Teams
        Microsoft 365
        Microsoft Office
        Office 365
        Skype
        Azure Data Studio
        Remote Desktop
        """, Applies.NotStore);

    private static readonly Group MicrosoftSync = new("OneDrive", Applies.NotStore);

    private static readonly Group MicrosoftPowerShell = new("PowerShell 7", Applies.NotStore);

    private static readonly Group MicrosoftPublisher = new("p:Microsoft");

    private static UninstallRisk? RateMicrosoft(Subject subject)
    {
        if (subject.Kind == UninstallEntryKind.StoreApp)
        {
            // A Store app is Microsoft's by its family's publisher id; a missing family falls back to the certificate.
            var microsoft = UninstallRiskClassifier.HasMicrosoftPublisherId(subject.Family)
                || (subject.Family.Length == 0 && MicrosoftPublisher.Matches(subject));
            if (!microsoft)
                return null;

            if (StorePowerShell.Matches(subject))
                return Rated(CleanupRiskLevel.Considered, UninstallRiskReason.DevRuntime);

            return StoreAllowlist.Matches(subject)
                ? Rated(CleanupRiskLevel.Considered, UninstallRiskReason.Microsoft)
                : Rated(CleanupRiskLevel.Risky, UninstallRiskReason.Microsoft);
        }

        if (subject.Kind != UninstallEntryKind.Program || !MicrosoftPublisher.Matches(subject))
            return null;

        if (MicrosoftPowerShell.Matches(subject))
            return Rated(CleanupRiskLevel.Considered, UninstallRiskReason.DevRuntime);

        if (MicrosoftSync.Matches(subject))
            return Rated(CleanupRiskLevel.Considered, UninstallRiskReason.Sync);

        return MicrosoftAllowlist.Matches(subject)
            ? Rated(CleanupRiskLevel.Considered, UninstallRiskReason.Microsoft)
            : Rated(CleanupRiskLevel.Risky, UninstallRiskReason.Microsoft);
    }

    // ---- 15 to 21.

    private static readonly Group Remote = new("""
        v:TeamViewer
        v:AnyDesk
        RustDesk
        v:Splashtop
        Parsec
        Chrome Remote Desktop Host
        RealVNC
        TightVNC
        UltraVNC
        Radmin
        v:LogMeIn
        v:GoTo
        GoToMeeting
        GoToAssist
        GoToMyPC
        Remote Utilities
        Zoho Assist
        Citrix Workspace
        p:Citrix
        VMware Horizon Client
        Omnissa Horizon Client
        Horizon Client
        v:Tailscale
        ZeroTier
        Hamachi
        OpenVPN
        WireGuard
        SoftEther
        NordVPN
        ExpressVPN
        Proton VPN
        Mullvad
        Surfshark
        CyberGhost
        Private Internet Access
        Windscribe
        Cloudflare WARP
        """);

    private static readonly Group Sync = new("""
        v:Dropbox
        Google Drive
        iCloud
        Nextcloud
        ownCloud
        Synology Drive Client
        Box Drive
        MEGAsync
        pCloud
        Proton Drive
        Acronis True Image
        Acronis Cyber Protect Home Office
        Macrium Reflect
        Veeam Agent
        EaseUS Todo Backup
        AOMEI Backupper
        Paragon Backup
        v:Backblaze
        Carbonite
        CrashPlan
        IDrive
        Synology Active Backup Agent
        Arq
        OneDrive
        """);

    private static readonly Group GamePlatform = new("""
        Epic Games Launcher
        Epic Online Services
        EA app
        Origin
        Ubisoft Connect
        Uplay
        Battle.net
        GOG Galaxy
        Riot Client
        Riot Vanguard
        Rockstar Games Launcher
        Amazon Games
        itch
        FACEIT AC
        ESEA
        PunkBuster
        nProtect GameGuard
        Easy Anti-Cheat
        BattlEye
        """);

    private static readonly Group InputMethod = new("""
        Keyman
        Google Japanese Input
        Sogou Pinyin
        MSKLC
        Microsoft Keyboard Layout Creator
        Keyboard Layout
        """);

    private static readonly Group DevRuntime = new("""
        re:python\s+(?:\d|launcher)
        Node.js
        NodeJS
        Java
        JRE
        JDK
        OpenJDK
        Temurin
        Amazon Corretto
        Corretto
        GraalVM
        Git
        PowerShell 7
        Go Programming Language
        re:rust[^()]*\((?:msvc|gnu)
        p:The Rust Project Developers
        Ruby
        Strawberry Perl
        ActivePerl
        PHP
        Erlang/OTP
        Erlang OTP
        Elixir
        R for Windows
        Julia
        Dart
        Deno
        Bun
        MSYS2
        Cygwin
        MinGW-w64
        LLVM
        CMake
        NVM for Windows
        Mercurial
        TortoiseHg
        PostgreSQL
        MySQL Server
        MariaDB
        MongoDB
        Redis
        """);

    private static readonly Group OemTool = new("""
        v:Dell
        v:HP|Hewlett Packard|Hewlett-Packard|HP Development Company|HP Inc
        v:Lenovo
        v:ASUS|ASUSTeK
        v:Acer
        v:MSI|Micro-Star
        v:Gigabyte|GIGA-BYTE
        v:ASRock
        v:Samsung
        Microsoft Surface
        v:Logitech
        v:Razer
        v:Corsair
        v:SteelSeries
        v:HyperX
        v:Turtle Beach|Roccat
        v:Cooler Master
        v:NZXT
        v:Lian Li
        v:Thermaltake
        v:be quiet!
        v:Deepcool
        v:EVGA
        v:Zotac
        p:Sapphire
        v:PowerColor
        v:Palit
        v:Gainward
        v:Inno3D
        v:Western Digital|WD
        v:SanDisk
        v:Seagate
        v:Micron|Crucial
        v:Kingston
        v:ADATA
        v:Sabrent
        v:Kioxia
        v:LG Electronics|LG
        v:BenQ
        v:Alienware
        v:Huawei
        v:Xiaomi
        p:Honor
        v:Dynabook
        v:Panasonic
        v:Getac
        v:Medion
        v:Clevo
        v:Tongfang
        v:XMG
        v:Tuxedo
        p:Framework
        v:Jabra
        p:GN
        p:Poly
        v:Plantronics
        v:EPOS|Sennheiser
        v:Bose
        v:Sony
        v:Garmin
        v:Fitbit
        v:APC|Schneider Electric
        v:Eaton
        v:CyberPower
        s:E046963F.
        s:E0469640.
        s:AD2F1837.
        s:B9ECED6F.
        s:DellInc.
        s:9426MICRO-STARINTERNATION.
        s:AcerIncorporated.
        s:SAMSUNGELECTRONICSCO.LTD.
        """);

    private static readonly Group RuntimeGeneric = new("""
        Runtime
        Runtimes
        Run Time
        Redistributable
        Redist
        Framework
        Libraries
        ODBC
        Connector
        Codec
        Codecs
        OpenAL
        Apple Application Support
        Bonjour
        Ghostscript
        GStreamer
        K-Lite Codec Pack
        LAV Filters
        MATLAB Runtime
        Crystal Reports
        Oracle Instant Client
        GTK
        Adobe AIR
        """);

    /// <summary>
    /// Steps 5 to 21 of the rule order, first match wins. The guest tools sit ahead of the Virtualization table and the
    /// Hardware name tokens behind it (the specified order: Broadcom owns VMware).
    /// </summary>
    internal static readonly IReadOnlyList<Func<Subject, UninstallRisk?>> Steps =
    [
        // 5.
        Step(DriverUpdaters, CleanupRiskLevel.Considered, UninstallRiskReason.OemTool),
        Step(Bundleware, CleanupRiskLevel.Considered, UninstallRiskReason.Ordinary),
        Step(VendorHelpers, CleanupRiskLevel.Considered, UninstallRiskReason.OemTool),
        Step(EdgeChannels, CleanupRiskLevel.Considered, UninstallRiskReason.Microsoft),

        // 6 to 10.
        Step(Accessibility, CleanupRiskLevel.Risky, UninstallRiskReason.Accessibility),
        Step(DataLoss, CleanupRiskLevel.Risky, UninstallRiskReason.DataLoss),
        Step(Security, CleanupRiskLevel.Risky, UninstallRiskReason.Security),
        Step(CompanyAccess, CleanupRiskLevel.Risky, UninstallRiskReason.CompanyAccess),
        Step(RuntimeRisky, CleanupRiskLevel.Risky, UninstallRiskReason.Runtime),

        // 12 (with the guest tools of 11 ahead of it), then the rest of 11 and 13.
        Step(GuestTools, CleanupRiskLevel.Risky, UninstallRiskReason.Hardware),
        Step(Virtualization, CleanupRiskLevel.Considered, UninstallRiskReason.Virtualization),
        Step(HardwareNames, CleanupRiskLevel.Risky, UninstallRiskReason.Hardware),
        Step(HardwareVendors, CleanupRiskLevel.Risky, UninstallRiskReason.Hardware),

        // 14 to 21.
        RateMicrosoft,
        Step(Remote, CleanupRiskLevel.Considered, UninstallRiskReason.Remote),
        Step(Sync, CleanupRiskLevel.Considered, UninstallRiskReason.Sync),
        Step(GamePlatform, CleanupRiskLevel.Considered, UninstallRiskReason.GamePlatform),
        Step(InputMethod, CleanupRiskLevel.Considered, UninstallRiskReason.InputMethod),
        Step(DevRuntime, CleanupRiskLevel.Considered, UninstallRiskReason.DevRuntime),
        Step(OemTool, CleanupRiskLevel.Considered, UninstallRiskReason.OemTool),
        Step(RuntimeGeneric, CleanupRiskLevel.Considered, UninstallRiskReason.Runtime),
    ];

    // ---- 4. The package id table, per source.

    private static readonly string[] DevRuntimePrefixes = ["python", "nodejs", "jre", "jdk", "openjdk", "temurin"];

    private static readonly string[] DevRuntimeNames = ["golang", "ruby"];

    /// <summary>Rule 4: a package the table names, by source and id; null when the table does not know it.</summary>
    internal static UninstallRisk? RatePackage(string? sourceId, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(packageId))
            return null;

        var source = UninstallRiskClassifier.Lower(sourceId.Trim());
        var id = UninstallRiskClassifier.Lower(packageId.Trim());
        var slash = id.LastIndexOf('/');
        if (source == "scoop" && slash >= 0)
            id = id[(slash + 1)..];

        return source switch
        {
            "chocolatey" => RateChocolatey(id),
            "scoop" => RateScoop(id),
            "pip" => Among(id, "pip", "setuptools") ? Itself() : null,
            "npm" or "pnpm" => Among(id, "npm", "corepack", "pnpm", "yarn") ? Itself() : null,
            "psgallery" => Among(id, "powershellget", "packagemanagement", "microsoft.powershell.psresourceget", "microsoft.winget.client") ? Itself() : null,
            "gcloud" => Among(id, "core", "gcloud", "bq", "gsutil") ? Itself() : null,
            "gem" => Among(id, "bundler", "rubygems-update") ? Itself() : null,
            "mise" => Among(id.Split('@')[0], "node", "python", "java", "go") ? Dev() : null,
            "volta" => Among(id.StartsWith("volta-", StringComparison.Ordinal) ? id["volta-".Length..] : id, "node", "npm", "yarn") ? Dev() : null,
            "pyenv-win" => Dev(),
            "android-sdk" => id is "platform-tools" or "emulator" || id.StartsWith("build-tools", StringComparison.Ordinal) || id.StartsWith("system-images", StringComparison.Ordinal) ? Dev() : null,
            _ => null,
        };
    }

    private static UninstallRisk? RateChocolatey(string id)
    {
        if (id.StartsWith("vcredist", StringComparison.Ordinal) || id.StartsWith("dotnet", StringComparison.Ordinal)
            || id.StartsWith("netfx-", StringComparison.Ordinal) || id is "directx" or "webview2-runtime")
            return Rated(CleanupRiskLevel.Risky, UninstallRiskReason.Runtime);

        if (IsKbNumber(id))
            return Rated(CleanupRiskLevel.Risky, UninstallRiskReason.Update);

        if (id == "chocolatey")
            return Itself();

        return IsDevRuntimePackage(id) || id == "git" ? Dev() : null;
    }

    private static UninstallRisk? RateScoop(string id)
    {
        if (id is "7zip" or "git")
            return Itself();

        return IsDevRuntimePackage(id) ? Dev() : null;
    }

    private static bool IsDevRuntimePackage(string id) =>
        DevRuntimePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal)) || DevRuntimeNames.Contains(id, StringComparer.Ordinal);

    private static bool IsKbNumber(string id) =>
        id.Length > 2 && id.StartsWith("kb", StringComparison.Ordinal) && id.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool Among(string id, params string[] names) => names.Contains(id, StringComparer.Ordinal);

    private static UninstallRisk Itself() => Rated(CleanupRiskLevel.Risky, UninstallRiskReason.PackageManagerItself);

    private static UninstallRisk Dev() => Rated(CleanupRiskLevel.Considered, UninstallRiskReason.DevRuntime);
}
