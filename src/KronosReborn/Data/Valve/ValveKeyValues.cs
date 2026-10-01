using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KronosReborn.Data.Valve;

/// <summary>
/// One node in a Valve key-values document: either a value, or a named block containing more nodes.
/// </summary>
/// <remarks>
/// The format is Valve's own, used by Steam for its configuration and application manifests. It is a
/// nested block structure with quoted strings, which is enough that it has been reimplemented many
/// times and a fair few of those implementations fail on the same things: an unterminated quoted
/// string, a stray closing brace, or a comment that runs to the end of the file.
///
/// Those are handled here by refusing to parse rather than by guessing. A file that cannot be read
/// is reported as unreadable, which is different from a file that is read as being empty, and the
/// difference matters: an unreadable library configuration must not be mistaken for a library with no
/// games.
/// </remarks>
internal sealed class ValveNode
{
    readonly List<ValveNode> _children = [];

    public ValveNode(string name, string? value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Key this node is stored under.</summary>
    public string Name { get; }

    /// <summary>
    /// The text value, or null when this node is a block containing other nodes.
    /// </summary>
    /// <remarks>
    /// Null rather than empty string, because "present and empty" and "absent" are different
    /// answers and a caller sometimes needs to tell them apart.
    /// </remarks>
    public string? Value { get; }

    /// <summary>Child nodes, empty for a value node.</summary>
    public IReadOnlyList<ValveNode> Children => _children;

    /// <summary>Whether this node is a block rather than a value.</summary>
    public bool IsBlock => Value is null;

    internal void Add(ValveNode child) => _children.Add(child);

    /// <summary>
    /// The first child with the given name, or null.
    /// </summary>
    /// <remarks>
    /// First rather than last, and case-insensitively, because the same key routinely appears more
    /// than once in these files and the first is the one the format defines as authoritative.
    /// </remarks>
    public ValveNode? Find(string name)
    {
        foreach (var child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// The value of the first child with the given name, or null if absent or if it is a block.
    /// </summary>
    public string? GetString(string name) => Find(name)?.Value;

    /// <summary>
    /// The value of the first child with the given name, as a 32-bit integer.
    /// </summary>
    /// <remarks>
    /// Missing or malformed gives null rather than zero. A zero is a legitimate value for some of
    /// these fields, so returning it for "could not read" would turn an unreadable file into a
    /// plausible-looking wrong answer.
    /// </remarks>
    public int? GetInt32(string name)
    {
        var value = GetString(name);

        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Every child with the given name, in document order.</summary>
    public IReadOnlyList<ValveNode> FindAll(string name)
    {
        var matches = new List<ValveNode>();

        foreach (var child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(child);
            }
        }

        return matches;
    }
}

/// <summary>
/// Reads Valve key-values documents.
/// </summary>
/// <remarks>
/// A recursive descent parser over a hand written tokenizer. The tokenizer is separate because the
/// awkward parts of this format are all lexical: escape sequences, comments, and the fact that a
/// string may be quoted or bare.
/// </remarks>
internal static class ValveKeyValues
{
    /// <summary>
    /// How deep nesting may go before the document is refused.
    /// </summary>
    /// <remarks>
    /// Real files nest perhaps four deep. A malformed or hostile file that opens braces without
    /// closing them would otherwise recurse until the stack overflows, which takes the process down
    /// rather than returning an error. Refusing is the difference between a library reporting itself
    /// unreadable and the program dying.
    /// </remarks>
    internal const int MaxDepth = 64;

    /// <summary>
    /// Parses a document, returning null if it cannot be read.
    /// </summary>
    public static ValveNode? Parse(string text) => TryParse(text, out var root, out _) ? root : null;

    /// <summary>
    /// Parses a document, reporting why if it cannot be read.
    /// </summary>
    /// <param name="text">The whole document.</param>
    /// <param name="root">
    /// The root node on success. A document that is only comments or whitespace yields null with no
    /// error, because that is genuinely empty rather than unreadable.
    /// </param>
    /// <param name="error">A description of the problem, or null.</param>
    public static bool TryParse(string text, out ValveNode? root, out string? error)
    {
        root = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        try
        {
            var position = 0;
            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                return true;
            }

            // Some files wrap everything in a named top level block, others put a bare key and value
            // at the top. Both shapes occur, and rejecting the second loses the whole file.
            var rootName = string.Empty;
            var token = ReadToken(text, ref position);

            if (!IsName(token.Kind))
            {
                error = $"Expected a key name but found {Describe(token.Kind)}.";
                return false;
            }

            rootName = token.Text;

            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                // A file containing only a key name is an empty value rather than an error, because
                // there is nothing in it to contradict.
                root = new ValveNode(rootName, string.Empty);
                return true;
            }

            if (text[position] == '{')
            {
                position++;
                var node = new ValveNode(rootName, null);
                SkipTrivia(text, ref position);

                if (!ParseBlockBody(text, ref position, node, 1))
                {
                    error = "Unterminated block: a '{' was never closed.";
                    return false;
                }

                root = node;
                return true;
            }

            // A bare key with no block: the value follows it directly.
            var value = ReadToken(text, ref position);

            if (!IsName(value.Kind))
            {
                error = $"Key '{rootName}' has no value.";
                return false;
            }

            root = new ValveNode(rootName, value.Text);
            return true;
        }
        catch (ValveParseException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static bool ParseBlockBody(string text, ref int position, ValveNode parent, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ValveParseException($"Nesting deeper than {MaxDepth} levels, which no real file reaches.");
        }

        while (true)
        {
            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                // Ran out of input with the block still open.
                return false;
            }

            if (text[position] == '}')
            {
                position++;
                return true;
            }

            var key = ReadToken(text, ref position);

            if (!IsName(key.Kind))
            {
                throw new ValveParseException($"Expected a key name but found {Describe(key.Kind)}.");
            }

            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                throw new ValveParseException($"Key '{key.Text}' has no value.");
            }

            if (text[position] == '{')
            {
                position++;
                var block = new ValveNode(key.Text, null);

                if (!ParseBlockBody(text, ref position, block, depth + 1))
                {
                    throw new ValveParseException($"Unterminated block for '{key.Text}'.");
                }

                parent.Add(block);
                continue;
            }

            var value = ReadToken(text, ref position);

            if (!IsName(value.Kind))
            {
                throw new ValveParseException($"Key '{key.Text}' has no value.");
            }

            parent.Add(new ValveNode(key.Text, value.Text));
        }
    }

    /// <summary>
    /// Whether a token can serve as a key or a value.
    /// </summary>
    /// <remarks>
    /// Both quoted and bare tokens can. Steam writes plenty of unquoted keys and values, and a reader
    /// that accepted only the quoted form would report every one of those files as malformed. Only the
    /// end of the input is not a name.
    /// </remarks>
    static bool IsName(TokenKind kind) => kind == TokenKind.String || kind == TokenKind.Symbol;

    /// <summary>
    /// Skips whitespace and comments.
    /// </summary>
    /// <remarks>
    /// Comments run from '//' to the end of the line. Steam also uses a conditional block syntax for
    /// platform specific keys, whose contents are bracketed by '[' and ']'. Those are skipped as a
    /// whole so that a platform section does not contribute keys the current platform should not see.
    /// </remarks>
    static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];

            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            if (c == '[')
            {
                while (position < text.Length && text[position] != ']')
                {
                    position++;
                }

                if (position < text.Length)
                {
                    position++;
                }

                continue;
            }

            return;
        }
    }

    static Token ReadToken(string text, ref int position)
    {
        SkipTrivia(text, ref position);

        if (position >= text.Length)
        {
            return new Token(TokenKind.EndOfInput, string.Empty);
        }

        return text[position] == '"'
            ? new Token(TokenKind.String, ReadQuoted(text, ref position))
            : new Token(TokenKind.Symbol, ReadBare(text, ref position));
    }

    static string ReadQuoted(string text, ref int position)
    {
        // Opening quote.
        position++;

        var builder = new StringBuilder();

        while (position < text.Length)
        {
            var c = text[position];

            if (c == '\\' && position + 1 < text.Length)
            {
                var next = text[position + 1];
                position += 2;

                builder.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    // Only the escapes the format actually defines are consumed. Anything else keeps
                    // its backslash, because Valve writes \x as a path separator rather than as an
                    // escape, and dropping it corrupts every Windows path in the file:
                    // C:\xSteam\common would become CxSteamcommon.
                    _ => string.Concat('\\', next),
                });

                continue;
            }

            if (c == '"')
            {
                position++;
                return builder.ToString();
            }

            builder.Append(c);
            position++;
        }

        // No closing quote anywhere in the remainder of the file.
        throw new ValveParseException("A quoted string was never closed.");
    }

    static string ReadBare(string text, ref int position)
    {
        var start = position;

        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '{' && text[position] != '}')
        {
            position++;
        }

        return text[start..position];
    }

    static string Describe(TokenKind kind) => kind switch
    {
        TokenKind.EndOfInput => "the end of the file",
        TokenKind.Symbol => "an unexpected symbol",
        _ => "an unexpected token",
    };

    enum TokenKind
    {
        String,
        Symbol,
        EndOfInput,
    }

    readonly record struct Token(TokenKind Kind, string Text);

    sealed class ValveParseException(string message) : Exception(message);
}