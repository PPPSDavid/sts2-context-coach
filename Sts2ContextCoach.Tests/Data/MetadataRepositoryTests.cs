using Sts2ContextCoach.Data;
using Xunit;

namespace Sts2ContextCoach.Tests.Data;

/// <summary>
/// Covers <see cref="MetadataRepository"/> JSON ingestion. Serialized via xUnit collection because
/// <see cref="MetadataRepository"/> uses process-wide static dictionaries cleared on each load.
/// </summary>
[CollectionDefinition("MetadataRepository", DisableParallelization = true)]
public sealed class MetadataRepositoryCollection;

[Collection("MetadataRepository")]
public sealed class MetadataRepositoryTests
{
    [Fact]
    public void LoadFromJson_IngestsCardsAndRelics()
    {
        const string cards = """
            {"schema_version":1,"cards":[{"internal_name":"iron_wave","display_name":"Iron Wave"}]}
            """;
        const string relics = """
            {"schema_version":1,"relics":[{"internal_name":"burning_blood","display_name":"Burning Blood"}]}
            """;

        MetadataRepository.LoadFromJson(cards, relics);

        Assert.True(MetadataRepository.TryGetCard("iron_wave", out var card));
        Assert.Equal("Iron Wave", card!.DisplayName);
        Assert.True(MetadataRepository.TryGetRelic("burning_blood", out var relic));
        Assert.Equal("Burning Blood", relic!.DisplayName);
    }

    [Fact]
    public void TryGetCard_ResolvesApostropheCollapsedForm_WhenCanonicalUsesWikiEncoding()
    {
        const string cards = """
            {"schema_version":1,"cards":[{"internal_name":"Ascender27sBane","display_name":"Ascender's Bane"}]}
            """;
        const string relics = """{"schema_version":1,"relics":[]}""";

        MetadataRepository.LoadFromJson(cards, relics);

        Assert.True(MetadataRepository.TryGetCard("AscendersBane", out var viaAlias));
        Assert.Equal("Ascender's Bane", viaAlias!.DisplayName);
    }
}
