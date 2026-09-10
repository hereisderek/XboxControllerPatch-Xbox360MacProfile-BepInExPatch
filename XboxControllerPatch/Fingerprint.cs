using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

// Ports just enough of the launcher's swift/Fingerprint.swift to compute the
// same machine hash from C#: SHA-256 of the Mac's hardware UUID (from
// `ioreg`), truncated to the length the ticket itself declares (see
// LicenseTicket.cs) rather than a hardcoded constant here - that way this
// side never has to be rebuilt in lockstep just because
// Fingerprint.hashLength changed on the Swift side.
public static class Fingerprint
{
    public static byte[] CurrentMachineHash(int length)
    {
        var raw = CurrentMachineIdRaw() ?? "unknown-machine";
        return TruncatedHash(raw, length);
    }

    private static byte[] TruncatedHash(string s, int length)
    {
        using (var sha = SHA256.Create())
        {
            var full = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            if (length < 0 || length > full.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            var result = new byte[length];
            Array.Copy(full, result, length);
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
