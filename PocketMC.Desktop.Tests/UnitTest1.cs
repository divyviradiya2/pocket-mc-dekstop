using PocketMC.Desktop.Utils;

namespace PocketMC.Desktop.Tests;

public class SlugHelperTests
{
    [Fact]
    public void GenerateSlug_NormalizesWhitespaceAndSymbols()
    {
        Assert.Equal("my-cool-server", SlugHelper.GenerateSlug(" My Cool Server! "));
    }

    [Fact]
    public void GenerateSlug_FallsBackWhenInputHasNoUsableCharacters()
    {
        Assert.Equal("unnamed-server", SlugHelper.GenerateSlug("!!!"));
    }
}
