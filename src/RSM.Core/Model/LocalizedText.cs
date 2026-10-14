namespace RSM.Core.Model;

public enum UiLanguage
{
    Fa,
    En,
}

/// <summary>A Persian/English text pair. Domain texts (rules, capabilities, KB) use this; UI chrome uses resx.</summary>
public sealed record LocalizedText(string Fa, string En)
{
    public static readonly LocalizedText Empty = new(string.Empty, string.Empty);

    public string Get(UiLanguage language) => language == UiLanguage.Fa ? Fa : En;

    public override string ToString() => En;
}
