using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopSplitter.Services;

/// <summary>
/// MAC-bound licensing for the control app. Pure .NET (no WPF references) so the
/// same file can be compiled into the ci/test-license console harness.
///
/// Model:
///   * First launch starts a 60-day trial automatically. The trial start is pinned in
///     two places (%ProgramData%\DesktopSplitter\license.json and HKCU) and bound to
///     the machine by an integrity tag, so a plain reinstall does not reset it.
///   * After the trial, splitting is disabled until the user pastes a license code.
///     A code is a signed payload: {"exp":"2027-05-16"|"never","mac":"AA-BB-..."}.
///     The payload is signed offline with the ECDSA P-256 private key; only the
///     public key is embedded here, so a code cannot be forged from the source.
///   * The signature is raw IEEE P1363 r||s (64 bytes) over the exact payload bytes.
///     The code is Base64Url(payload) + "." + Base64Url(signature).
///
/// Honest limit: this is local enforcement in a public repository. It stops casual
/// reuse; it cannot stop someone who rebuilds the app without the check.
/// </summary>
public static class LicenseService
{
    public const int TrialDays = 60;

    /// <summary>Shown when the trial is over and no valid license is present.</summary>
    public const string NotLicensedMessage = "未注册，请联系管理员";

    /// <summary>ECDSA P-256 public key (SPKI DER, Base64). The private key is held offline.</summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/A3MCbWx68wCHzSPksoJo/GiLn82/tPSf9A6/tISu05zz7RfopN44K/8CtdLFq6EozKqVUHqvDrE8gYDYo+yKg==";

    private const string StateDirectoryName = "DesktopSplitter";
    private const string TrialFileName = "license.json";
    private const string CodeFileName = "license.txt";
    private const string RegistryPath = @"Software\DesktopSplitter\License";

    // Integrity tag for the trial record. Tamper-evident only (the key is in the source);
    // the registry mirror is what actually survives a deleted file.
    private static readonly byte[] TrialTagKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("DesktopSplitter-License-v1"));

    public enum LicenseState
    {
        Licensed,
        Trial,
        Expired,
    }

    public sealed class LicenseStatus
    {
        public required LicenseState State { get; init; }
        public int TrialDaysLeft { get; init; }
        public DateTime? LicensedUntilUtc { get; init; }
        public required string PrimaryMac { get; init; }

        public bool CanApply => State != LicenseState.Expired;

        public string DisplayText => State switch
        {
            LicenseState.Licensed => LicensedUntilUtc is null
                ? "已授权（永久）"
                : "已授权，有效期至 " + LicensedUntilUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LicenseState.Trial => "试用剩余 " + TrialDaysLeft + " 天",
            _ => NotLicensedMessage,
        };
    }

    public enum CodeVerification
    {
        Valid,
        Malformed,
        BadSignature,
        MacMismatch,
        Expired,
    }

    public sealed class CodeResult
    {
        public required CodeVerification Verification { get; init; }
        public DateTime? ExpiresUtc { get; init; }

        public string Message => Verification switch
        {
            CodeVerification.Valid => ExpiresUtc is null ? "授权成功（永久）。" : "授权成功，有效期至 " + ExpiresUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "。",
            CodeVerification.MacMismatch => "授权码与本机不匹配，请确认本机 MAC 地址。",
            CodeVerification.Expired => "授权码已过期。",
            CodeVerification.BadSignature => "授权码无效（签名校验失败）。",
            _ => "授权码格式不正确。",
        };
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Current status. Creates the trial record on first launch.</summary>
    public static LicenseStatus GetStatus()
    {
        string primaryMac = GetPrimaryMac();
        IReadOnlyList<string> macs = GetPhysicalMacs();

        string? code = LoadStoredCode();
        if (!string.IsNullOrWhiteSpace(code))
        {
            CodeResult result = VerifyCode(code, PublicKeyBase64, macs);
            if (result.Verification == CodeVerification.Valid)
            {
                return new LicenseStatus
                {
                    State = LicenseState.Licensed,
                    LicensedUntilUtc = result.ExpiresUtc,
                    PrimaryMac = primaryMac,
                };
            }
        }

        (DateTime firstRunUtc, DateTime lastSeenUtc) = LoadOrCreateTrial();
        DateTime effectiveNow = DateTime.UtcNow > lastSeenUtc ? DateTime.UtcNow : lastSeenUtc;
        if (DateTime.UtcNow > lastSeenUtc)
        {
            SaveTrial(firstRunUtc, DateTime.UtcNow);
        }

        double daysLeft = (firstRunUtc.AddDays(TrialDays) - effectiveNow).TotalDays;
        if (daysLeft <= 0)
        {
            return new LicenseStatus { State = LicenseState.Expired, PrimaryMac = primaryMac };
        }

        return new LicenseStatus
        {
            State = LicenseState.Trial,
            TrialDaysLeft = Math.Max(1, (int)Math.Ceiling(daysLeft)),
            PrimaryMac = primaryMac,
        };
    }

    /// <summary>Verifies a pasted code and, if valid, stores it as the machine license.</summary>
    public static CodeResult ActivateCode(string code)
    {
        CodeResult result = VerifyCode(code, PublicKeyBase64, GetPhysicalMacs());
        if (result.Verification == CodeVerification.Valid)
        {
            try { SaveStoredCode(CompactCode(code)); } catch { /* activation still counts for this run */ }
        }
        return result;
    }

    private static string CompactCode(string code) =>
        new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// Verifies a code against a public key and a set of machine MACs. The public key is a
    /// parameter (rather than the embedded one) so the CI harness can test with a throwaway key.
    /// </summary>
    public static CodeResult VerifyCode(string? code, string publicKeyBase64, IReadOnlyList<string> machineMacs)
    {
        if (string.IsNullOrWhiteSpace(code)) return new CodeResult { Verification = CodeVerification.Malformed };

        string compact = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        string[] parts = compact.Split('.');
        if (parts.Length != 2) return new CodeResult { Verification = CodeVerification.Malformed };

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64UrlDecode(parts[0]);
            signature = Base64UrlDecode(parts[1]);
        }
        catch
        {
            return new CodeResult { Verification = CodeVerification.Malformed };
        }

        if (signature.Length != 64) return new CodeResult { Verification = CodeVerification.Malformed };

        bool signatureOk;
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            signatureOk = ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return new CodeResult { Verification = CodeVerification.Malformed };
        }

        if (!signatureOk) return new CodeResult { Verification = CodeVerification.BadSignature };

        string mac;
        string exp;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;
            mac = NormalizeMac(root.GetProperty("mac").GetString() ?? string.Empty);
            exp = root.GetProperty("exp").GetString() ?? string.Empty;
        }
        catch
        {
            return new CodeResult { Verification = CodeVerification.Malformed };
        }

        if (string.IsNullOrEmpty(mac) || string.IsNullOrEmpty(exp))
        {
            return new CodeResult { Verification = CodeVerification.Malformed };
        }

        if (!machineMacs.Contains(mac, StringComparer.OrdinalIgnoreCase))
        {
            return new CodeResult { Verification = CodeVerification.MacMismatch };
        }

        if (exp == "never") return new CodeResult { Verification = CodeVerification.Valid, ExpiresUtc = null };

        if (!DateTime.TryParseExact(exp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime expDate))
        {
            return new CodeResult { Verification = CodeVerification.Malformed };
        }

        // Valid through the end of the expiry date (UTC).
        DateTime expiresUtc = expDate.AddDays(1);
        if (DateTime.UtcNow >= expiresUtc)
        {
            return new CodeResult { Verification = CodeVerification.Expired, ExpiresUtc = expiresUtc };
        }

        return new CodeResult { Verification = CodeVerification.Valid, ExpiresUtc = expiresUtc };
    }

    // ------------------------------------------------------------------ MAC addresses

    /// <summary>MAC the license window shows (the one the user sends to the licensor).</summary>
    public static string GetPrimaryMac()
    {
        IReadOnlyList<NetworkInterface> physical = GetPhysicalAdapters();
        NetworkInterface? up = physical.FirstOrDefault(n =>
            n.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
            n.OperationalStatus == OperationalStatus.Up)
            ?? physical.FirstOrDefault(n =>
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                n.OperationalStatus == OperationalStatus.Up)
            ?? physical.FirstOrDefault();
        return up is null ? string.Empty : FormatMac(up.GetPhysicalAddress());
    }

    public static IReadOnlyList<string> GetPhysicalMacs() =>
        GetPhysicalAdapters().Select(FormatMac).Where(m => m.Length == 17).ToList();

    private static IReadOnlyList<NetworkInterface> GetPhysicalAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.Wireless80211)
                .Where(n => !IsVirtual(n.Description))
                .ToList();
        }
        catch
        {
            return Array.Empty<NetworkInterface>();
        }
    }

    private static bool IsVirtual(string description)
    {
        string d = description.ToLowerInvariant();
        return d.Contains("virtual") || d.Contains("vmware") || d.Contains("virtualbox")
            || d.Contains("hyper-v") || d.Contains("tap-") || d.Contains("wireguard")
            || d.Contains("vpn") || d.Contains("bluetooth");
    }

    private static string FormatMac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 6) return string.Empty;
        return string.Join("-", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public static string NormalizeMac(string mac)
    {
        string hex = new string(mac.Where(char.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return string.Empty;
        return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    // ------------------------------------------------------------------ trial store

    private static (DateTime firstRunUtc, DateTime lastSeenUtc) LoadOrCreateTrial()
    {
        DateTime? earliestFirst = null;
        DateTime? latestSeen = null;

        (DateTime? fileFirst, DateTime? fileSeen) = LoadTrialFile();
        if (fileFirst is not null)
        {
            earliestFirst = fileFirst.Value;
            latestSeen = fileSeen;
        }

        (DateTime? regFirst, DateTime? regSeen) = LoadTrialRegistry();
        if (regFirst is not null)
        {
            earliestFirst = earliestFirst is null ? regFirst.Value : Min(earliestFirst.Value, regFirst.Value);
            latestSeen = latestSeen is null ? regSeen : Max(latestSeen.Value, regSeen ?? latestSeen.Value);
        }

        if (earliestFirst is null)
        {
            DateTime now = DateTime.UtcNow;
            SaveTrial(now, now);
            return (now, now);
        }

        return (earliestFirst.Value, latestSeen ?? earliestFirst.Value);
    }

    private static void SaveTrial(DateTime firstRunUtc, DateTime lastSeenUtc)
    {
        SaveTrialFile(firstRunUtc, lastSeenUtc);
        SaveTrialRegistry(firstRunUtc, lastSeenUtc);
    }

    private static string TrialFilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                StateDirectoryName);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, TrialFileName);
        }
    }

    private static (DateTime? first, DateTime? seen) LoadTrialFile()
    {
        try
        {
            if (!File.Exists(TrialFilePath)) return (null, null);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(TrialFilePath));
            JsonElement root = doc.RootElement;
            string first = root.GetProperty("firstRunUtc").GetString() ?? string.Empty;
            string seen = root.GetProperty("lastSeenUtc").GetString() ?? string.Empty;
            string tag = root.GetProperty("tag").GetString() ?? string.Empty;
            if (!VerifyTrialTag(first, seen, tag)) return (null, null);
            return (ParseUtc(first), ParseUtc(seen));
        }
        catch
        {
            return (null, null);
        }
    }

    private static void SaveTrialFile(DateTime firstRunUtc, DateTime lastSeenUtc)
    {
        try
        {
            string first = firstRunUtc.ToString("o", CultureInfo.InvariantCulture);
            string seen = lastSeenUtc.ToString("o", CultureInfo.InvariantCulture);
            string json = JsonSerializer.Serialize(new
            {
                firstRunUtc = first,
                lastSeenUtc = seen,
                tag = ComputeTrialTag(first, seen),
            });
            File.WriteAllText(TrialFilePath, json);
        }
        catch
        {
            // Trial still counts for this run; the registry mirror is the fallback.
        }
    }

    private static (DateTime? first, DateTime? seen) LoadTrialRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null) return (null, null);
            return (ParseUtc(key.GetValue("TrialFirstRunUtc") as string),
                ParseUtc(key.GetValue("TrialLastSeenUtc") as string));
        }
        catch
        {
            return (null, null);
        }
    }

    private static void SaveTrialRegistry(DateTime firstRunUtc, DateTime lastSeenUtc)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("TrialFirstRunUtc", firstRunUtc.ToString("o", CultureInfo.InvariantCulture));
            key.SetValue("TrialLastSeenUtc", lastSeenUtc.ToString("o", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Registry write failing must not block the app.
        }
    }

    private static string ComputeTrialTag(string firstRunUtc, string lastSeenUtc)
    {
        using HMACSHA256 hmac = new(TrialTagKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(firstRunUtc + "|" + lastSeenUtc)));
    }

    private static bool VerifyTrialTag(string firstRunUtc, string lastSeenUtc, string tag) =>
        string.Equals(ComputeTrialTag(firstRunUtc, lastSeenUtc), tag, StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : null;

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    // ------------------------------------------------------------------ stored code

    private static string? LoadStoredCode()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                StateDirectoryName, CodeFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveStoredCode(string code)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            StateDirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, CodeFileName), code);
    }

    // ------------------------------------------------------------------ base64url

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        return Convert.FromBase64String(s);
    }
}
