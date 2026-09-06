using System;
using System.Security.Cryptography;

// Verifies the short-lived "this launch is authorized" ticket the launcher
// hands down via an environment variable (see swift/AuthTicket.swift for the
// full rationale and format) - nothing is ever written to disk for this.
//
// This is deliberately the ONLY authorization check in this project: it does
// not duplicate a trial counter or accept a permanent serial directly,
// because every legitimate authorization (trial or paid) already flows
// through the launcher, which always mints a fresh ticket right before
// launch. A bare copy of this DLL, run without ever going through the
// launcher, never gets a valid ticket and stays inert.
//
// If you found this file because you're building XboxControllerPatch from
// its public source: yes, you can simply remove this check (or the call to
// it in Patcher.cs) in your own build. That's an accepted gap, not a bug -
// see the "Honesty note" in the top-level project README. What this check
// stops is our own precompiled DLL working, unmodified, on a machine that
// never ran our launcher.
public static class LicenseTicket
{
    // Must match AuthTicket.sharedSecretHex in swift/AuthTicket.swift exactly.
    private const string SharedSecretHex = "9e711cb7a5139be81c91f2e4aa6204a7b952e207a9adb2d65081f95f02923167";

    // Must match AuthTicket.environmentVariableName in swift/AuthTicket.swift exactly.
    private const string EnvironmentVariableName = "OC2XBOXPATCH_TICKET";

    private const int PayloadLength = 24; // 16-byte machine hash + 8-byte timestamp
    private const int TagLength = 32;     // HMAC-SHA256 output
    private const int TicketLength = PayloadLength + TagLength;

    private const string LogPrefix = "[XboxPatch][License] ";

    public static bool IsAuthorized(TimeSpan maxAge)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrEmpty(value))
            {
                Log("No ticket received (" + EnvironmentVariableName + " is not set) - not authorized.");
                return false;
            }
            Log("Received ticket (" + value.Length + " chars) from " + EnvironmentVariableName + ".");

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(value.Trim());
            }
            catch (FormatException)
            {
                Log("Ticket is not valid base64 - rejecting.");
                return false;
            }

            if (raw.Length != TicketLength)
            {
                Log("Ticket has the wrong length (" + raw.Length + " bytes, expected " + TicketLength + ") - rejecting.");
                return false;
            }

            var payload = new byte[PayloadLength];
            var tag = new byte[TagLength];
            Array.Copy(raw, 0, payload, 0, PayloadLength);
            Array.Copy(raw, PayloadLength, tag, 0, TagLength);

            using (var hmac = new HMACSHA256(HexToBytes(SharedSecretHex)))
            {
                var expected = hmac.ComputeHash(payload);
                if (!ConstantTimeEquals(expected, tag))
                {
                    Log("Ticket signature does not match - rejecting (tampered, corrupted, or minted with a different secret).");
                    return false;
                }
            }
            Log("Ticket signature valid.");

            var machineHash = new byte[16];
            Array.Copy(payload, 0, machineHash, 0, 16);

            long ts = ReadInt64BigEndian(payload, 16);
            long now = ToUnixTimeSeconds(DateTime.UtcNow);
            long age = now - ts;
            if (age > maxAge.TotalSeconds || ts - now > 60)
            {
                Log("Ticket is stale (age " + age + "s, max " + maxAge.TotalSeconds + "s) - rejecting.");
                return false;
            }
            Log("Ticket is fresh (age " + age + "s).");

            var currentMachineHash = Fingerprint.CurrentMachineHash();
            if (!ConstantTimeEquals(currentMachineHash, machineHash))
            {
                Log("Ticket's machine hash does not match this machine - rejecting.");
                return false;
            }

            Log("Ticket verified - authorized.");
            return true;
        }
        catch (Exception ex)
        {
            Log("Ticket verification threw an exception - rejecting. " + ex);
            return false;
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine(LogPrefix + message);
    }

    private static long ReadInt64BigEndian(byte[] buffer, int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            value = (value << 8) | buffer[offset + i];
        }
        return value;
    }

    private static long ToUnixTimeSeconds(DateTime utc)
    {
        return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }
        return diff == 0;
    }
}
