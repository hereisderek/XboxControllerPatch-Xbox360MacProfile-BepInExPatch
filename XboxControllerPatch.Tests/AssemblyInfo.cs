using Xunit;

// LicenseTicketTests mutates the process-global OC2XBOXPATCH_TICKET
// environment variable, so its test methods (and anything else sharing this
// collection) must never run concurrently with each other.
[CollectionDefinition("EnvironmentVariable", DisableParallelization = true)]
public class EnvironmentVariableCollection
{
}
