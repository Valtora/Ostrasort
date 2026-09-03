using System.Text.Json;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The repair pass that makes Ostrasort read what the GAME reads. Every "the
/// game accepts this" claim below was checked against the decompiled LitJson
/// lexer the game loads its data with.
/// </summary>
public class GameJsonTests
{
    private static string Relax(string text)
    {
        GameJson.TryRelax(text, out var relaxed);
        return relaxed;
    }

    [Fact]
    public void ValidJson_IsLeftAlone()
    {
        const string json = """[{"strName":"Mod","strNotes":"one\ntwo","n":1.5,"b":true}]""";
        Assert.False(GameJson.TryRelax(json, out var relaxed));
        Assert.Same(json, relaxed);
    }

    [Fact]
    public void RawControlCharactersInsideAString_AreEscaped()
    {
        // the reported case: a line break pasted into strNotes
        Assert.Equal(@"{""a"":""x\ny""}", Relax("{\"a\":\"x\ny\"}"));
        Assert.Equal(@"{""a"":""x\r\ny""}", Relax("{\"a\":\"x\r\ny\"}"));
        Assert.Equal(@"{""a"":""x\ty""}", Relax("{\"a\":\"x\ty\"}"));
        Assert.Equal(@"{""a"":""x\u0001y""}", Relax("{\"a\":\"x\u0001y\"}"));
    }

    [Fact]
    public void LineBreaksBetweenTokens_AreNotTouched()
    {
        const string json = "[\n  {\"a\":\"x\"}\n]";
        Assert.False(GameJson.TryRelax(json, out _));
    }

    [Fact]
    public void SingleQuotedStrings_BecomeDoubleQuoted()
    {
        Assert.Equal("""{"a":"x"}""", Relax("{'a':'x'}"));
        Assert.Equal("""{"a":"say \"hi\""}""", Relax("{'a':'say \"hi\"'}"));
    }

    [Fact]
    public void EscapedApostrophe_LosesItsBackslash()
    {
        // LitJson reads \' as a bare apostrophe; strict JSON rejects the escape
        Assert.Equal("""{"a":"don't"}""", Relax(@"{""a"":""don\'t""}"));
    }

    [Fact]
    public void ApostropheInsideAComment_IsNotReadAsAString()
    {
        const string json = "[\n  // don't touch this\n  {\"a\":\"x\"}\n]";
        Assert.False(GameJson.TryRelax(json, out _));

        const string block = "[\n  /* it's fine */\n  {\"a\":\"x\"}\n]";
        Assert.False(GameJson.TryRelax(block, out _));
    }

    [Fact]
    public void TrailingCommas_AreNotRepaired()
    {
        // the game's loader ERRORs on these, so they must stay reportable
        Assert.False(GameJson.TryRelax("""[{"a":"x"},]""", out _));
        Assert.False(GameJson.TryRelax("""[{"a":"x",}]""", out _));
    }

    [Fact]
    public void UnterminatedString_StaysUnterminated()
    {
        // repairing it would turn a genuinely broken file into a parseable one
        Assert.ThrowsAny<JsonException>(() => GameJson.Parse("{\"a\":\"x\ny}", default));
    }

    [Fact]
    public void Parse_ReportsTheFaultTheGameWouldHit_NotTheOneItTolerates()
    {
        // a raw line break AND a trailing comma: the repair fixes the first,
        // and the message must describe the second, which is what breaks the
        // game load
        var e = Assert.ThrowsAny<JsonException>(
            () => GameJson.Parse("[{\"a\":\"x\ny\",}]", default));
        Assert.Contains("trailing comma", e.Message);
    }

    [Fact]
    public void ParseNode_ReadsAValueHoldingARawNewline()
    {
        var node = GameJson.ParseNode("[{\"strName\":\"X\",\"strNotes\":\"a\nb\"}]", null, default);
        Assert.Equal("a\nb", node![0]!["strNotes"]!.GetValue<string>());
    }
}
