using System;
using System.Diagnostics;
using System.IO;
using Xunit;

// LicenseTicketTests proves LicenseTicket.IsAuthorized correctly verifies a
// ticket built to the right byte format; AuthTicketTests (in the parent
// repo's own Swift test suite) proves AuthTicket.swift produces a
// well-formed one. Neither ever actually runs the two against each other -
// this class does: it compiles the real, unmodified swift/{Base32,
// Fingerprint,SerialFormat,AuthTicket}.swift from the parent repo, runs it
// to mint a real ticket for this real machine, and feeds that exact string
// into the real, unmodified LicenseTicket.IsAuthorized here. This is the
// only place that checks the two sides actually agree, byte for byte, on a
// live machine - not just that each independently matches the documented
// format.
//
// patch/ is also meant to be usable as a fully standalone checkout (see its
// own README) with no parent repo present - so this skips itself (rather
// than failing) whenever it can't find the parent repo or a working
// `swiftc`, instead of requiring them.
[Collection("EnvironmentVariable")]
public class RealLauncherInteropTests : IDisposable
{
    private const string EnvVar = "OC2XBOXPATCH_TICKET";

    public void Dispose() => Environment.SetEnvironmentVariable(EnvVar, null);

    // Walks up from the test binary's own location looking for the parent
    // repo (build.sh + swift/ alongside it) - patch/ is checked out as a
    // submodule somewhere under it, at an otherwise-unpredictable depth.
    private static string FindParentRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build.sh")) &&
                Directory.Exists(Path.Combine(dir.FullName, "swift")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static bool TryRunProcess(string exe, string[] args, out string stdout, out string stderr)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            stdout = p.StandardOutput.ReadToEnd();
            stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            stdout = null;
            stderr = null;
            return false;
        }
    }

    // Compiles a throwaway Swift binary from the parent repo's real
    // swift/*.swift (no forked copy) that mints one ticket via the real
    // AuthTicket.installIntoEnvironment and prints it, then runs it.
    // hardwareId, if given, mints for that raw ID instead of this real
    // machine - used to build a deliberately-wrong ticket.
    private static string MintRealTicket(string repoRoot, string workDir, string hardwareId)
    {
        var swiftDir = Path.Combine(repoRoot, "swift");
        var mainSwiftPath = Path.Combine(workDir, "main.swift");
        var machineHashExpr = hardwareId == null
            ? "Fingerprint.current().machineHash"
            : $"Fingerprint.truncatedHash(\"{hardwareId}\")";
        File.WriteAllText(mainSwiftPath, $@"
import Foundation
AuthTicket.installIntoEnvironment(machineHash: {machineHashExpr})
print(ProcessInfo.processInfo.environment[AuthTicket.environmentVariableName]!)
");

        var binPath = Path.Combine(workDir, "mint");
        var compileArgs = new[]
        {
            "-O",
            Path.Combine(swiftDir, "Base32.swift"),
            Path.Combine(swiftDir, "Fingerprint.swift"),
            Path.Combine(swiftDir, "SerialFormat.swift"),
            Path.Combine(swiftDir, "AuthTicket.swift"),
            mainSwiftPath,
            "-o", binPath,
        };
        if (!TryRunProcess("swiftc", compileArgs, out _, out var compileError))
        {
            throw new InvalidOperationException("Failed to compile the real AuthTicket.swift for interop testing: " + compileError);
        }

        if (!TryRunProcess(binPath, Array.Empty<string>(), out var stdout, out var runError))
        {
            throw new InvalidOperationException("Failed to run the compiled ticket-minting binary: " + runError);
        }
        return stdout.Trim();
    }

    private static void RunIfAvailable(Action<string, string> test)
    {
        var repoRoot = FindParentRepoRoot();
        if (repoRoot == null || !TryRunProcess("swiftc", new[] { "--version" }, out _, out _))
        {
            return; // standalone patch/ checkout, or no Swift toolchain here - nothing to interop-test against
        }

        var tempDir = Directory.CreateTempSubdirectory("oc2xbox-interop-").FullName;
        try
        {
            test(repoRoot, tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RealTicket_ForThisMachine_IsAcceptedByRealLicenseTicket()
    {
        RunIfAvailable((repoRoot, tempDir) =>
        {
            var ticket = MintRealTicket(repoRoot, tempDir, hardwareId: null);
            Environment.SetEnvironmentVariable(EnvVar, ticket);

            Assert.True(
                LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)),
                "a ticket minted by the real AuthTicket.swift for this machine must be accepted by the real LicenseTicket.IsAuthorized"
            );
        });
    }

    [Fact]
    public void RealTicket_ForADifferentMachine_IsRejectedByRealLicenseTicket()
    {
        RunIfAvailable((repoRoot, tempDir) =>
        {
            var ticket = MintRealTicket(repoRoot, tempDir, hardwareId: "some-other-machines-hardware-uuid");
            Environment.SetEnvironmentVariable(EnvVar, ticket);

            Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
        });
    }

    [Fact]
    public void RealTicket_Tampered_IsRejectedByRealLicenseTicket()
    {
        RunIfAvailable((repoRoot, tempDir) =>
        {
            var ticket = MintRealTicket(repoRoot, tempDir, hardwareId: null);
            var raw = Convert.FromBase64String(ticket);
            raw[raw.Length - 1] ^= 0xFF;
            Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(raw));

            Assert.False(LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(5)));
        });
    }
}
