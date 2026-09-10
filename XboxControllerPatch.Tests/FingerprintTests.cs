using Xunit;

public class FingerprintTests
{
    [Fact]
    public void CurrentMachineHash_IsDeterministic()
    {
        Assert.Equal(Fingerprint.CurrentMachineHash(8), Fingerprint.CurrentMachineHash(8));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void CurrentMachineHash_ReturnsRequestedLength(int length)
    {
        Assert.Equal(length, Fingerprint.CurrentMachineHash(length).Length);
    }

    [Fact]
    public void CurrentMachineHash_ShorterLengthIsAPrefixOfLonger()
    {
        // Both are a prefix of the same SHA-256 digest, so a shorter truncation
        // must equal the start of a longer one computed from the same input -
        // this is what lets LicenseTicket verify a ticket built at any hash
        // length without this side needing to know that length in advance.
        var full = Fingerprint.CurrentMachineHash(32);
        var half = Fingerprint.CurrentMachineHash(16);

        for (int i = 0; i < half.Length; i++)
        {
            Assert.Equal(full[i], half[i]);
        }
    }
}
