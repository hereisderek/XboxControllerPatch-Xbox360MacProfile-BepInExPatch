using System;
using System.Security.Cryptography;
using Xunit;

// Builds tickets the exact way AuthTicket.swift does (see the top-level
// project's swift/AuthTicket.swift) and feeds them through the real
// LicenseTicket.IsAuthorized to check the verification side on its own,
// without needing to run the Swift launcher. Every test sets and restores
// OC2XBOXPATCH_TICKET (process-global state), so this class must not run its
// methods in parallel with each other or with FingerprintTests.
[Collection("EnvironmentVariable")]
public class LicenseTicketTests : IDisposable
{
    // Must match LicenseTicket.SharedSecretHex / AuthTicket.sharedSecretHex exactly.
    private const string SharedSecretHex = "9e711cb7a5139be81c91f2e4aa6204a7b952e207a9adb2d65081f95f02923167";
    private const string EnvVar = "OC2XBOXPATCH_TICKET";

    public void Dispose() => Environment.SetEnvironmentVariable(EnvVar, null);

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    // payload = machineHashLength(1) | machineHash(N) | unix timestamp (8, big-endian)
    // ticket  = payload | HMAC-SHA256(payload)
    private static string BuildTicket(byte[] machineHash, DateTimeOffset timestamp, byte[] secretOverride = null)
    {
        var payload = new byte[1 + machineHash.Length + 8];
        payload[0] = (byte)machineHash.Length;
        Array.Copy(machineHash, 0, payload, 1, machineHash.Length);

        long ts = timestamp.ToUnixTimeSeconds();
        for (int i = 0; i < 8; i++)
        {
            payload[1 + machineHash.Length + i] = (byte)(ts >> (8 * (7 - i)));
        }

        using (var hmac = new HMACSHA256(secretOverride ?? HexToBytes(SharedSecretHex)))
        {
            var tag = hmac.ComputeHash(payload);
            var ticket = new byte[payload.Length + tag.Length];
            Array.Copy(payload, ticket, payload.Length);
            Array.Copy(tag, 0, ticket, payload.Length, tag.Length);
            return Convert.ToBase64String(ticket);
        }
    }

    private static byte[] CurrentMachineHash(int length) => Fingerprint.CurrentMachineHash(length);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void ValidTicket_AnyHashLength_IsAuthorized(int hashLength)
    {
        var machineHash = CurrentMachineHash(hashLength);
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(machineHash, DateTimeOffset.UtcNow));

        Assert.True(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void NoTicketSet_IsNotAuthorized()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void NotBase64_IsNotAuthorized()
    {
        Environment.SetEnvironmentVariable(EnvVar, "not-valid-base64!!!");

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void WrongMachineHash_IsNotAuthorized()
    {
        var wrongHash = new byte[8];
        RandomNumberGenerator.Fill(wrongHash);
        // Astronomically unlikely to collide with the real current machine hash.
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(wrongHash, DateTimeOffset.UtcNow));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void StaleTicket_PastMaxAge_IsNotAuthorized()
    {
        var machineHash = CurrentMachineHash(8);
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(machineHash, DateTimeOffset.UtcNow.AddMinutes(-10)));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void FutureTicket_BeyondClockSkew_IsNotAuthorized()
    {
        var machineHash = CurrentMachineHash(8);
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(machineHash, DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void FutureTicket_WithinClockSkew_IsAuthorized()
    {
        var machineHash = CurrentMachineHash(8);
        // IsAuthorized tolerates up to 60s of clock skew ahead of "now".
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(machineHash, DateTimeOffset.UtcNow.AddSeconds(30)));

        Assert.True(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TamperedSignature_IsNotAuthorized()
    {
        var machineHash = CurrentMachineHash(8);
        var raw = Convert.FromBase64String(BuildTicket(machineHash, DateTimeOffset.UtcNow));
        raw[raw.Length - 1] ^= 0xFF; // flip a bit in the trailing HMAC tag
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(raw));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void WrongSecret_IsNotAuthorized()
    {
        var machineHash = CurrentMachineHash(8);
        var forgedSecret = new byte[32];
        RandomNumberGenerator.Fill(forgedSecret);
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(machineHash, DateTimeOffset.UtcNow, forgedSecret));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DeclaredHashLengthZero_IsNotAuthorized()
    {
        // Build the raw bytes directly instead of via BuildTicket, since a
        // zero-length "hash" isn't representable as a byte[] the normal way.
        var payload = new byte[1 + 0 + 8];
        payload[0] = 0;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < 8; i++) payload[1 + i] = (byte)(ts >> (8 * (7 - i)));

        using var hmac = new HMACSHA256(HexToBytes(SharedSecretHex));
        var tag = hmac.ComputeHash(payload);
        var ticket = new byte[payload.Length + tag.Length];
        Array.Copy(payload, ticket, payload.Length);
        Array.Copy(tag, 0, ticket, payload.Length, tag.Length);
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(ticket));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DeclaredHashLengthTooLarge_IsNotAuthorized()
    {
        // 33 bytes is longer than a full SHA-256 digest (32) - can't be a real
        // hash, so LicenseTicket should reject it outright rather than trying
        // to read 33 bytes of "hash" out of whatever bytes happen to follow.
        var fakeHash = new byte[33];
        RandomNumberGenerator.Fill(fakeHash);
        Environment.SetEnvironmentVariable(EnvVar, BuildTicket(fakeHash, DateTimeOffset.UtcNow));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TruncatedTicket_ShorterThanDeclaredLength_IsNotAuthorized()
    {
        var machineHash = CurrentMachineHash(16);
        var raw = Convert.FromBase64String(BuildTicket(machineHash, DateTimeOffset.UtcNow));
        // Chop off the last few bytes so the declared length (16) no longer
        // matches how much data actually follows.
        var truncated = new byte[raw.Length - 5];
        Array.Copy(raw, truncated, truncated.Length);
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(truncated));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void EmptyTicket_IsNotAuthorized()
    {
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(Array.Empty<byte>()));

        Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
    }
}
