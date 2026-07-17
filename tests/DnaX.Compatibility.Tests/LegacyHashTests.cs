using DnaX.Compatibility;

namespace DnaX.Compatibility.Tests;

public sealed class LegacyHashTests
{
    [Theory]
    [InlineData("", 3_074_457_345_618_258_791UL)]
    [InlineData("a", 13_322_648_497_679_176_632UL)]
    [InlineData("DNA", 17_877_400_122_760_236_034UL)]
    [InlineData("Happening", 12_434_297_966_700_276_985UL)]
    public void MatchesOriginalDnaVectors(string value, ulong expected) =>
        Assert.Equal(expected, LegacyHash.Knuth(value));

    [Fact]
    public void ParamsOverloadConcatenatesAndTreatsNullAsEmpty() =>
        Assert.Equal(LegacyHash.Knuth("DNA"), LegacyHash.Knuth("D", null, "NA"));
}
