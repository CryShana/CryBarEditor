using CryBar;

namespace CryBar.Tests;

public class StringTableParserTests
{
    [Fact]
    public void SingleLineEntries()
    {
        var content = """
            ID = "STR_CULTURE_GREEKS"   ;   Str = "Greeks"
            ID = "STR_CULTURE_EGYPTIANS"   ;   Str = "Egyptians"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Greeks", result["STR_CULTURE_GREEKS"]);
        Assert.Equal("Egyptians", result["STR_CULTURE_EGYPTIANS"]);
    }

    [Fact]
    public void SingleLineWithSymbol()
    {
        var content = """
            ID = "STR_CIV_ZEUS"   ;   Str = "Zeus"   ;   Symbol = "cStringCivZeus"
            ID = "STR_CIV_HADES"   ;   Str = "Hades"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Zeus", result["STR_CIV_ZEUS"]);
        Assert.Equal("Hades", result["STR_CIV_HADES"]);
    }

    [Fact]
    public void MultiLineValue()
    {
        var content = """
            ID = "STR_CIV_ZEUS_LR"   ;   Str = "Focus: Infantry and Heroes.
            • Starts with 15 Favor.
            • Gains Favor 20% faster."
            ID = "STR_CIV_HADES"   ;   Str = "Hades"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("Focus: Infantry and Heroes.", result["STR_CIV_ZEUS_LR"]);
        Assert.Contains("Starts with 15 Favor.", result["STR_CIV_ZEUS_LR"]);
        Assert.Contains("Gains Favor 20% faster.", result["STR_CIV_ZEUS_LR"]);
        Assert.Equal("Hades", result["STR_CIV_HADES"]);
    }

    [Fact]
    public void UnicodeContent()
    {
        var content = """
            Language = "Czech"
            IsRtl = "False"

            ID = "STR_CULTURE_GREEKS"   ;   Str = "Řekové"
            ID = "STR_CULTURE_NORSE"   ;   Str = "Seveřané"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Řekové", result["STR_CULTURE_GREEKS"]);
        Assert.Equal("Seveřané", result["STR_CULTURE_NORSE"]);
    }

    [Fact]
    public void CommentsAndBlankLinesIgnored()
    {
        var content = """
            // Major Gods
            // Greek

            ID = "STR_CIV_ZEUS"   ;   Str = "Zeus"

            // Norse
            ID = "STR_CIV_ODIN"   ;   Str = "Odin"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Zeus", result["STR_CIV_ZEUS"]);
        Assert.Equal("Odin", result["STR_CIV_ODIN"]);
    }

    [Fact]
    public void CaseInsensitiveLookup()
    {
        var content = """
            ID = "STR_UNIT_HOPLITE_NAME"   ;   Str = "Hoplite"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Equal("Hoplite", result["str_unit_hoplite_name"]);
    }

    [Fact]
    public void FindValue_ReturnsMatchingKey()
    {
        var content = """
            ID = "STR_A"   ;   Str = "Alpha"
            ID = "STR_B"   ;   Str = "Beta"
            """;

        Assert.Equal("Beta", StringTableParser.FindValue(content, "STR_B"));
    }

    [Fact]
    public void FindValue_ReturnsNullForMissingKey()
    {
        var content = """
            ID = "STR_A"   ;   Str = "Alpha"
            """;

        Assert.Null(StringTableParser.FindValue(content, "STR_MISSING"));
    }

    [Fact]
    public void EmptyContent()
    {
        var result = StringTableParser.Parse("");
        Assert.Empty(result);
    }

    [Fact]
    public void MultiLineValue_LastEntry()
    {
        // Multi-line value as the LAST entry (no next ID to terminate)
        var content = """
            ID = "STR_DESCRIPTION"   ;   Str = "Line one.
            Line two.
            Line three."
            """;

        var result = StringTableParser.Parse(content);

        Assert.Single(result);
        Assert.StartsWith("Line one.", result["STR_DESCRIPTION"]);
        Assert.Contains("Line two.", result["STR_DESCRIPTION"]);
        Assert.Contains("Line three.", result["STR_DESCRIPTION"]);
    }

    [Fact]
    public void MissingIdClosingQuote_Skipped()
    {
        // Malformed ID line - no closing quote on ID key
        var content = """
            ID = "STR_BROKEN ;   Str = "hello"
            ID = "STR_GOOD"   ;   Str = "world"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("world", result["STR_GOOD"]);
    }

    [Fact]
    public void DoubleQuoteInId_Skipped()
    {
        var content = """
            ID = "STR_BAD""   ;   Str = "hello"
            ID = "STR_GOOD"   ;   Str = "world"
            """;

        var result = StringTableParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("world", result["STR_GOOD"]);
    }

    [Fact]
    public void MissingStrClosingQuote_NextIdNotCorrupted()
    {
        // Str value missing closing quote - next ID should still parse correctly
        var content = """
            ID = "STR_BROKEN"   ;   Str = "no end quote
            ID = "STR_GOOD"   ;   Str = "world"
            """;

        var result = StringTableParser.Parse(content);

        // STR_BROKEN should capture something (best-effort), but STR_GOOD must not be corrupted
        Assert.Equal(2, result.Count);
        Assert.Equal("world", result["STR_GOOD"]);
    }
}
