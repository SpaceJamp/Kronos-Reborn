using System;
using System.Linq;
using KronosReborn.Data.Valve;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the Valve key-values reader.
/// </summary>
/// <remarks>
/// This is the first code that reads a file the program did not write, and files from other software
/// are exactly where a parser meets input that violates its own assumptions. The happy paths are
/// cheap; the tests worth having are the ones that assert a malformed file is *reported* rather than
/// guessed at or crashed on.
///
/// Two properties matter more than correctness of individual values:
///
///   - An unreadable file must be distinguishable from an empty one. A library whose configuration
///     cannot be read looks identical to a library with no games if both become an empty list, and the
///     user is told the wrong thing.
///   - Nothing may throw. The program reads these files during discovery, where an escaping exception
///     would take down discovery for every library rather than for the one that is broken.
/// </remarks>
public class ValveKeyValuesTests
{
    [Fact]
    public void ReadsAKeyValuePairAtTheTopLevel()
    {
        // A document that is just a key and a value, with no wrapping block. The root node then holds
        // the value directly rather than containing it as a child, so it is read from Value and not
        // through GetString. Both shapes occur in the wild and both are supported.
        var root = ValveKeyValues.Parse("""
            "appid" "730"
            """);

        Assert.NotNull(root);
        Assert.Equal("appid", root!.Name);
        Assert.Equal("730", root.Value);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void ReadsNestedBlocks()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "appid" "730"
                "StateFlags" "4"
                "UserConfig"
                {
                    "language" "english"
                }
            }
            """);

        Assert.NotNull(root);
        Assert.Equal("730", root!.GetString("appid"));
        Assert.Equal("4", root.GetString("StateFlags"));
        Assert.Equal("english", root.Find("UserConfig")?.GetString("language"));
    }

    [Fact]
    public void ReadsBareUnquotedValues()
    {
        // Steam writes plenty of these. A parser that only accepts quoted values reads the file as
        // malformed and loses the whole library.
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                appid 730
                name Half-Life
            }
            """);

        Assert.NotNull(root);
        Assert.Equal("730", root!.GetString("appid"));
        Assert.Equal("Half-Life", root.GetString("name"));
    }

    [Fact]
    public void SkipsLineComments()
    {
        var root = ValveKeyValues.Parse("""
            // Written by Steam
            "AppState"
            {
                // the id
                "appid" "730"
            }
            """);

        Assert.NotNull(root);
        Assert.Equal("730", root!.GetString("appid"));
    }

    [Fact]
    public void SkipsPlatformConditionalSections()
    {
        // Steam brackets platform specific keys. Their contents should not surface as if they were
        // unconditional, or a Mac section would be read as the current configuration.
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "appid" "730"
                [$WINDOWS]
                {
                    " installdir" "WindowsGame"
                }
                [$LINUX]
                {
                    "installdir" "LinuxGame"
                }
            }
            """);

        Assert.NotNull(root);
        Assert.Equal("730", root!.GetString("appid"));
        Assert.Null(root.GetString("installdir"));
        Assert.Empty(root.FindAll("installdir"));
    }

    [Fact]
    public void UnescapesQuotedValues()
    {
        // Wrapped in a block, as real files are, so this tests the escapes rather than the shape of a
        // document that is a bare key and value.
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "path" "C:\\Games\\Thing"
                "spaced" "a \"quoted\" word"
            }
            """);

        Assert.NotNull(root);
        Assert.Equal(@"C:\Games\Thing", root!.GetString("path"));
        Assert.Equal("a \"quoted\" word", root.GetString("spaced"));
    }

    [Fact]
    public void KeepsWindowsPathsReadable()
    {
        // Valve writes "\x" as a separator rather than as an escape. Reading that as an escape and
        // dropping the backslash would silently eat characters out of every path in the file, turning
        // C:\xSteam\steamapps\common into CxSteamsteamappscommon.
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "installdir" "C:\xSteam\steamapps\common\Thing"
            }
            """);

        Assert.Equal(@"C:\xSteam\steamapps\common\Thing", root?.GetString("installdir"));
    }

    [Fact]
    public void KeysAreMatchedCaseInsensitively()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "AppID" "730"
            }
            """);

        Assert.Equal("730", root?.GetString("appid"));
    }

    [Fact]
    public void ReturnsTheFirstOfARepeatedKey()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "name" "first"
                "name" "second"
            }
            """);

        Assert.Equal("first", root?.GetString("name"));
        Assert.Equal(2, root!.FindAll("name").Count);
    }

    // ------------------------------------------------------------ malformed input

    [Fact]
    public void AnUnterminatedQuoteIsReportedRatherThanThrowing()
    {
        // Deliberately truncated mid-string. An earlier version of this test used an input that turned
        // out to be perfectly valid once bare keys were accepted, so it no longer tested anything.
        var ok = ValveKeyValues.TryParse("""
            "AppState"
            {
                "name" "no closing quote
            """, out var root, out var error);

        Assert.False(ok);
        Assert.Null(root);
        Assert.NotNull(error);
    }

    [Fact]
    public void AnUnclosedBlockIsReportedRatherThanThrowing()
    {
        var ok = ValveKeyValues.TryParse("""
            "AppState"
            {
                "appid" "730"
            """, out var root, out var error);

        Assert.False(ok);
        Assert.Null(root);
        Assert.NotNull(error);
    }

    [Fact]
    public void RunawayNestingIsRefusedRatherThanOverflowingTheStack()
    {
        // A file of nothing but opening braces would recurse until the process dies. Refusing is what
        // lets the caller report the library as unreadable instead.
        var text = new string('"', 1) + "root\"" + Environment.NewLine
                 + new string('{', 5000);

        var ok = ValveKeyValues.TryParse(text, out var root, out var error);

        Assert.False(ok);
        Assert.Null(root);
        Assert.NotNull(error);
    }

    [Fact]
    public void EmptyAndWhitespaceInputIsEmptyRatherThanUnreadable()
    {
        // The distinction the whole design turns on: no games is not the same as a broken file.
        Assert.True(ValveKeyValues.TryParse("", out var empty, out var emptyError));
        Assert.Null(empty);
        Assert.Null(emptyError);

        Assert.True(ValveKeyValues.TryParse("   \n\t  \n", out var blank, out var blankError));
        Assert.Null(blank);
        Assert.Null(blankError);

        // Comments only is also empty, not broken.
        Assert.True(ValveKeyValues.TryParse("// nothing here\n// nor here\n", out var comments, out var commentsError));
        Assert.Null(comments);
        Assert.Null(commentsError);
    }

    [Theory]
    [InlineData("\"key\"")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("\"a\" \"b\" \"c\"")]
    public void MalformedInputNeverThrows(string text)
    {
        // Whichever way it fails, the contract is that TryParse reports rather than propagating.
        var exception = Record.Exception(() => ValveKeyValues.TryParse(text, out _, out _));

        Assert.Null(exception);
    }

    [Fact]
    public void MalformedInputIsAlwaysSafeToParse()
    {
        // A byte-level fuzz over the shapes these files come in. Not a substitute for reasoning about
        // the parser, but it catches the unterminated-quote-and-truncated-file combinations that hand
        // written tokenizers get wrong.
        string[] pieces = ["\"", "{", "}", "//", "\n", "key", "\\", "[", "]", " ", "x"];

        var random = new Random(20260930);

        for (var i = 0; i < 400; i++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 24))
                .Select(_ => pieces[random.Next(pieces.Length)]));

            var exception = Record.Exception(() => ValveKeyValues.TryParse(text, out _, out _));

            Assert.Null(exception);
        }
    }

    // ------------------------------------------------------------ value access

    [Fact]
    public void AMissingKeyIsNullRatherThanEmpty()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "appid" "730"
            }
            """);

        Assert.Null(root?.GetString("installdir"));
        Assert.Null(root?.GetString("emptypresent"));
    }

    [Fact]
    public void AnUnreadableIntegerIsNullRatherThanZero()
    {
        // Zero is a legitimate value for several of these fields, so returning it for "could not read"
        // would turn a broken file into a plausible-looking wrong answer.
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "appid" "not a number"
            }
            """);

        Assert.Null(root?.GetInt32("appid"));
        Assert.Null(root?.GetInt32("absent"));
    }

    [Fact]
    public void ReadsIntegersInvariantly()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "size" "1234567"
                "negative" "-42"
            }
            """);

        Assert.Equal(1234567, root?.GetInt32("size"));
        Assert.Equal(-42, root?.GetInt32("negative"));
    }

    [Fact]
    public void ABlockIsNotAValue()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "UserConfig"
                {
                    "language" "english"
                }
            }
            """);

        var block = root?.Find("UserConfig");

        Assert.NotNull(block);
        Assert.True(block!.IsBlock);
        Assert.Null(block.Value);
        Assert.Null(block.GetString("UserConfig"));
    }

    [Fact]
    public void AnEmptyBlockIsStillABlock()
    {
        var root = ValveKeyValues.Parse("""
            "AppState"
            {
                "UserConfig"
                {
                }
            }
            """);

        Assert.True(root?.Find("UserConfig")?.IsBlock);
    }
}
