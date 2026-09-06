using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

// Ports just enough of the launcher's swift/Fingerprint.swift to compute the
// same machine hash from C#: SHA-256 of the Mac's hardware UUID (from
// `ioreg`), truncated to 16 bytes. Must stay byte-for-byte identical to the
// Swift side, or LicenseTicket's machine-hash comparison will never match.
public static class Fingerprint
{
    public static byte[] CurrentMachineHash()
    {
        var raw = CurrentMachineIdRaw() ?? "unknown-machine";
        return Sha256Truncated16(raw);
    }

    private static byte[] Sha256Truncated16(string s)
    {
        using (var sha = SHA256.Create())
        {
            var full = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var result = new byte[16];
            Array.Copy(full, result, 16);
            return result;
        }
    }

    // Hardware UUID, e.g. "57E34247-AB19-5270-BCC3-A0A77F4051CF"
    private static string CurrentMachineIdRaw()
    {
        try
        {
            var output = RunShell("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice");
            if (output == null) return null;
            foreach (var rawLine in output.Split('\n'))
            {
                if (rawLine.IndexOf("IOPlatformUUID", StringComparison.Ordinal) < 0) continue;
                var parts = rawLine.Split('"');
                if (parts.Length >= 4) return parts[3];
            }
        }
        catch
        {
            // Fall through to null - an unreadable/unexpected ioreg output just
            // means the machine hash won't match anything, same as Swift's fallback.
        }
        return null;
    }

    private static string RunShell(string path, string args)
    {
        var psi = new ProcessStartInfo(path, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (var p = Process.Start(psi))
        {
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }
    }
}
