using System.Security.Cryptography;
using System.Text;
using DesktopSplitter.Services;

// License self-test: runs in CI on the throwaway runner (dotnet run, no UI).
// Exits non-zero on the first failure; build.ps1 fails the build on that.

int failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

// ------------------------------------------------------------------ test vector
// Generated OFFLINE by gen_license.py --test-key (throwaway key, never the real
// one). This proves the Python generator and the C# verifier agree on the exact
// wire format: compact JSON payload, ECDSA P-256 / SHA-256, raw r||s signature,
// Base64Url(payload) + "." + Base64Url(signature).
const string TestPublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsnTUErbv4GTnDL2FAF349OwqW5NsH1VgDR8wzG1e/R7iezqRqhsSwIaaffmnfouRLhxG1VpWf7OzBpPlUwT8VQ==";
const string TestCode =
    "eyJleHAiOiIyMDMwLTAxLTAxIiwibWFjIjoiMDAtMTEtMjItMzMtNDQtNTUifQ.mOOwL0UYccyHS5dYNznSNwgSgaUUryQDKXVB-k4R0R41zeP7MKHRofHErIS84YOMEvV_GWoU9V6DT_jvnTXBAg";

string[] testMacs = { "00-11-22-33-44-55" };

LicenseService.CodeResult vector = LicenseService.VerifyCode(TestCode, TestPublicKey, testMacs);
Check("python-generated code verifies (cross-language)", vector.Verification == LicenseService.CodeVerification.Valid);
Check("python-generated code expiry reads back", vector.ExpiresUtc?.Date == new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc).Date);

// ------------------------------------------------------------------ C# round-trip
using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
string pubB64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

string SignCode(string payloadJson, ECDsa signingKey)
{
    byte[] payload = Encoding.UTF8.GetBytes(payloadJson);
    byte[] sig = signingKey.SignData(payload, HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return LicenseService.Base64UrlEncode(payload) + "." + LicenseService.Base64UrlEncode(sig);
}

string[] macs = { "AA-BB-CC-DD-EE-FF" };
string good = SignCode("{\"exp\":\"2030-06-01\",\"mac\":\"AA-BB-CC-DD-EE-FF\"}", key);
LicenseService.CodeResult roundTrip = LicenseService.VerifyCode(good, pubB64, macs);
Check("c# sign/verify round-trip", roundTrip.Verification == LicenseService.CodeVerification.Valid);

string tampered = good[..^1] + (good[^1] == 'A' ? 'B' : 'A');
Check("tampered code rejected",
    LicenseService.VerifyCode(tampered, pubB64, macs).Verification == LicenseService.CodeVerification.BadSignature);

Check("wrong MAC rejected",
    LicenseService.VerifyCode(TestCode, TestPublicKey, macs).Verification == LicenseService.CodeVerification.MacMismatch);

string expired = SignCode("{\"exp\":\"2020-01-01\",\"mac\":\"AA-BB-CC-DD-EE-FF\"}", key);
Check("expired code rejected",
    LicenseService.VerifyCode(expired, pubB64, macs).Verification == LicenseService.CodeVerification.Expired);

string permanent = SignCode("{\"exp\":\"never\",\"mac\":\"AA-BB-CC-DD-EE-FF\"}", key);
LicenseService.CodeResult perm = LicenseService.VerifyCode(permanent, pubB64, macs);
Check("permanent code valid", perm.Verification == LicenseService.CodeVerification.Valid && perm.ExpiresUtc is null);

Check("malformed code rejected",
    LicenseService.VerifyCode("not-a-code", pubB64, macs).Verification == LicenseService.CodeVerification.Malformed);

Check("pasted code with whitespace accepted",
    LicenseService.VerifyCode("  " + good + "\r\n", pubB64, macs).Verification == LicenseService.CodeVerification.Valid);

// ------------------------------------------------------------------ trial
LicenseService.LicenseStatus status = LicenseService.GetStatus();
Check("first launch starts a 60-day trial",
    status.State == LicenseService.LicenseState.Trial && status.TrialDaysLeft == 60);
Check("trial status text", status.DisplayText == "试用剩余 60 天");

Console.WriteLine(failures == 0 ? "LICENSE-TEST: all checks passed" : $"LICENSE-TEST: {failures} check(s) failed");
return failures == 0 ? 0 : 1;
