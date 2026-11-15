using TidalStudio.Core.Tidal;

namespace TidalStudio.Tests;

public class TidalCodeTests
{
    private const string Doc = "setcps 0.5\n\nd1 $ s \"bd sn\"\n  # n 1\n\n\nd2 $ s \"hh*8\"\nhush";

    [Fact]
    public void Line_is_just_the_caret_line()
    {
        var r = CodeBlocks.GetLine(Doc, 2)!;
        Assert.Equal("d1 $ s \"bd sn\"", r.Text);
        Assert.Equal(2, r.StartLine);
        Assert.Equal(Doc.IndexOf("d1", StringComparison.Ordinal), r.StartOffset);
    }

    [Fact]
    public void Block_is_paragraph_between_blank_lines()
    {
        var r = CodeBlocks.GetBlock(Doc, 3)!;
        Assert.Equal("d1 $ s \"bd sn\"\n  # n 1", r.Text);
        Assert.Equal((2, 3), (r.StartLine, r.EndLine));

        var last = CodeBlocks.GetBlock(Doc, 7)!;
        Assert.Equal("d2 $ s \"hh*8\"\nhush", last.Text);
    }

    [Fact]
    public void Blank_line_gives_nothing()
    {
        Assert.Null(CodeBlocks.GetBlock(Doc, 1));
        Assert.Null(CodeBlocks.GetLine(Doc, 4));
        Assert.Null(CodeBlocks.GetBlock(Doc, 99));
    }

    [Fact]
    public void Crlf_documents_work()
    {
        string crlf = Doc.Replace("\n", "\r\n");
        var r = CodeBlocks.GetBlock(crlf, 2)!;
        Assert.Equal("d1 $ s \"bd sn\"\n  # n 1", r.Text);
        Assert.Equal(crlf.IndexOf("d1", StringComparison.Ordinal), r.StartOffset);
    }

    [Fact]
    public void Selection_expands_to_whole_lines()
    {
        int start = Doc.IndexOf("bd", StringComparison.Ordinal);
        int end = Doc.IndexOf("n 1", StringComparison.Ordinal) + 1;
        Assert.Equal("d1 $ s \"bd sn\"\n  # n 1", CodeBlocks.GetSelection(Doc, start, end)!.Text);
    }

    private static PreprocessResult Wrap(string code)
    {
        int id = 0;
        return Preprocessor.Wrap(code, () => ++id);
    }

    [Fact]
    public void Strings_are_wrapped_with_ids_and_offsets()
    {
        var r = Wrap("d1 $ s \"bd sn\" # n \"<0 3>\"");
        Assert.Equal("d1 $ s (tsctx 1 \"bd sn\") # n (tsctx 2 \"<0 3>\")", r.Code);
        Assert.Equal(new[] { new LiteralRef(1, 7, 5), new LiteralRef(2, 19, 5) }, r.Literals);
    }

    [Fact]
    public void Comments_chars_commands_and_escapes_are_left_alone()
    {
        string code = "-- \"not this\"\n{- \"nor {- nested \"x\" -} this\" -}\nlet c = '\"'\n:t \"bd\"\nimport \"base\" Data.List\nd1 $ s \"a\\\"b\" -- \"x\"\nd1' = s \"ok\"";
        var r = Wrap(code);
        Assert.Single(r.Literals);
        Assert.Contains("(tsctx 1 \"ok\")", r.Code);
        Assert.Equal(code.Replace("\"ok\"", "(tsctx 1 \"ok\")"), r.Code);
    }

    [Fact]
    public void Operator_with_dashes_is_not_a_comment()
    {
        var r = Wrap("x --> s \"bd\"");
        Assert.Single(r.Literals);
    }

    [Fact]
    public void Empty_strings_and_lines_are_preserved()
    {
        var r = Wrap("once $ s \"\"\n\nd1 $ s \"x\"");
        Assert.Equal("once $ s \"\"\n\nd1 $ s (tsctx 1 \"x\")", r.Code);
        Assert.Equal(3, r.Code.Split('\n').Length);
    }

    [Fact]
    public void Ghci_errors_map_to_block_lines()
    {
        var lines = new[]
        {
            "<interactive>:125:8: error: [GHC-83865]",
            "    * Couldn't match expected type: Pattern Note",
            "<interactive>:(126,3)-(127,9): error:",
            "    * Something",
            "<interactive>:10:1: warning: [-Wfoo]",
            "    blah",
        };
        var errors = GhciHost.ParseErrors(lines, firstCodeLine: 124, lineCount: 4);
        Assert.Equal(2, errors.Count);
        Assert.Equal((1, 8), (errors[0].Line, errors[0].Column));
        Assert.Contains("Pattern Note", errors[0].Message);
        Assert.Equal(2, errors[1].Line);
    }

    [Theory]
    [InlineData("ghci> ghci| ghci| [TidalCycles version 1.10.1]", "[TidalCycles version 1.10.1]")]
    [InlineData("tidal> tidal> #TSREADY#", "#TSREADY#")]
    [InlineData("Prelude> 42", "42")]
    [InlineData("plain output", "plain output")]
    [InlineData("", "")]
    public void Prompts_are_stripped_from_output(string input, string expected) =>
        Assert.Equal(expected, GhciHost.StripPrompts(input));

    [Fact]
    public void Built_in_boot_file_has_visual_support()
    {
        var boot = BootFiles.FromBuiltIn();
        Assert.True(boot.SupportsVisuals);
        Assert.Contains("57130", boot.Text);
        Assert.Contains("d1 = p 1", boot.Text);
    }
}
