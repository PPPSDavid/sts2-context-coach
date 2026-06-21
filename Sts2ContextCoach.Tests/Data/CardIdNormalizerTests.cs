using Sts2ContextCoach.Data;
using Xunit;

namespace Sts2ContextCoach.Tests.Data;

public sealed class CardIdNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("AlreadyPascal", "AlreadyPascal")]
    [InlineData("iron_wave", "IronWave")]
    [InlineData(" dual_wield ", "DualWield")]
    public void FromModelIdEntry_NormalizesAsExpected(string? input, string expected)
    {
        var actual = CardIdNormalizer.FromModelIdEntry(input);
        Assert.Equal(expected, actual);
    }
}
