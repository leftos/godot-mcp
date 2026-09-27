using System.Globalization;
using System.Text;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Reads a member path left to right: a name, then <c>.name</c>, <c>[integer]</c> or <c>["key"]</c> (with <c>\"</c> and
/// <c>\\</c> escapes) until the end. A path may start with an index.
/// </summary>
internal sealed class MemberPathParser(string text)
{
    private int _position;

    public List<MemberPathSegment> ParseAll()
    {
        List<MemberPathSegment> segments = [];
        if (!text.StartsWith('['))
        {
            segments.Add(ReadName());
        }
        while (_position < text.Length)
        {
            segments.Add(ReadNext());
        }
        return segments;
    }

    private MemberPathSegment ReadNext()
    {
        switch (text[_position])
        {
            case '.':
                _position++;
                return ReadName();
            case '[':
                return ReadIndex();
            default:
                throw Fail("expected '.' or '['", _position);
        }
    }

    private MemberSegment ReadName()
    {
        int start = _position;
        while (_position < text.Length && text[_position] != '.' && text[_position] != '[')
        {
            _position++;
        }
        string name = text[start.._position];
        if (name.Length == 0)
        {
            throw Fail("expected a member name", start);
        }
        return IsIdentifier(name) ? new MemberSegment(name) : throw Fail($"'{name}' is not a C# identifier", start);
    }

    private MemberPathSegment ReadIndex()
    {
        int open = _position++;
        if (_position < text.Length && text[_position] == '"')
        {
            return ReadKey(open);
        }
        int close = text.IndexOf(']', _position);
        if (close < 0)
        {
            throw Fail("unclosed '['", open);
        }
        if (!int.TryParse(text.AsSpan(_position, close - _position), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            throw Fail("expected an integer or a quoted key", _position);
        }
        _position = close + 1;
        return new IndexSegment(index);
    }

    private KeySegment ReadKey(int open)
    {
        _position++;
        StringBuilder key = new();
        while (_position < text.Length && text[_position] != '"')
        {
            key.Append(ReadKeyChar());
        }
        if (_position + 1 >= text.Length)
        {
            throw Fail("unclosed '['", open);
        }
        if (text[_position + 1] != ']')
        {
            throw Fail("expected ']'", _position + 1);
        }
        _position += 2;
        return new KeySegment(key.ToString());
    }

    private char ReadKeyChar()
    {
        if (text[_position] == '\\' && _position + 1 < text.Length)
        {
            _position++;
        }
        return text[_position++];
    }

    private static bool IsIdentifier(string name) => (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private MemberPathException Fail(string what, int at) => new($"member path '{text}': {what} at {at}");
}
