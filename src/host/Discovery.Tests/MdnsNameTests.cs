using Xunit;

namespace ButtonBox.Discovery.Tests;

public sealed class MdnsNameTests
{
    // The name is shown to users and typed into the responder; a malformed generated name would break both.
    [Fact]
    public void Generated_names_have_the_documented_format_and_are_valid()
    {
        for (var i = 0; i < 200; i++)
        {
            var name = MdnsName.Generate();
            Assert.Matches("^buttonbox-[0-9a-f]{4}$", name);
            Assert.True(MdnsName.IsValid(name));
        }
    }

    // 4 random hex digits are the only thing that tells installations apart; a constant would make every PC collide.
    [Fact]
    public void Generated_names_vary()
    {
        var names = Enumerable.Range(0, 200).Select(_ => MdnsName.Generate()).Distinct().Count();

        Assert.True(names > 50, $"only {names} distinct names in 200");
    }

    // IsValid guards a value read back from disk: it must not accept anything that could break DNS encoding.
    [Theory]
    [InlineData("buttonbox-7f3a", true)]
    [InlineData("buttonbox-0000", true)]
    [InlineData("buttonbox-7F3A", false)]
    [InlineData("buttonbox-7f3", false)]
    [InlineData("buttonbox-7f3a1", false)]
    [InlineData("buttonbox-7f3g", false)]
    [InlineData("buttonbox-7f3a.local", false)]
    [InlineData("Buttonbox-7f3a", false)]
    [InlineData("xbuttonbox-7f3a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_accepts_only_prefix_plus_four_lowercase_hex_digits(string? label, bool expected)
    {
        Assert.Equal(expected, MdnsName.IsValid(label));
    }
}
