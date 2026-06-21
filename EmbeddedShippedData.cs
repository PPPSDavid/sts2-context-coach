using System.IO;
using System.Reflection;

namespace Sts2ContextCoach;

/// <summary>Shipped JSON is embedded so the game does not treat <c>mods/.../*.json</c> as extra mod manifests.</summary>
internal static class EmbeddedShippedData
{
    private const string CardsRes = "Sts2ContextCoach.Data.cards.json";
    private const string RelicsRes = "Sts2ContextCoach.Data.relics.json";
    private const string KeywordsRes = "Sts2ContextCoach.Data.keywords.json";

    public static bool TryLoadAll(out string? cardsJson, out string? relicsJson, out string? keywordsJson)
    {
        cardsJson = Read(CardsRes);
        relicsJson = Read(RelicsRes);
        keywordsJson = Read(KeywordsRes);
        return cardsJson != null && relicsJson != null && keywordsJson != null;
    }

    private static string? Read(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(resourceName);
        if (s == null)
            return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
