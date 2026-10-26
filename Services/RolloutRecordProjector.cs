using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AIMaster.Services;

// Read just the fields used by task/usage monitoring. In particular, neither complete
// nor unfinished image strings and tool output bodies become a resident raw line.
internal sealed class RolloutRecordProjector : IDisposable
{
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly JsonFilter _filter = new(Kind.Root);
    private readonly MemoryStream _output = new();
    private bool _invalid, _completed;

    public long PendingLength => _filter.RetainedBytes + _output.Length;
    public long Capacity => _filter.RetainedBytes + _output.Capacity;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_completed) Reset();
        if (_invalid) return;
        Span<char> characters = stackalloc char[2048];
        try
        {
            while (!bytes.IsEmpty)
            {
                _decoder.Convert(bytes, characters, false, out var used, out var written, out _);
                _filter.Feed(characters[..written]);
                bytes = bytes[used..];
            }
        }
        catch (DecoderFallbackException) { _invalid = true; }
    }

    // The returned projection stays valid until Reset/Append. Incomplete/malformed
    // records have no effect; the caller resets at the next JSONL line boundary.
    public ReadOnlyMemory<byte> CompleteRecord()
    {
        if (_completed) return _output.GetBuffer().AsMemory(0, (int)_output.Length);
        _completed = true;
        try
        {
            Span<char> final = stackalloc char[2];
            _decoder.Convert(ReadOnlySpan<byte>.Empty, final, true, out _, out var written, out _);
            _filter.Feed(final[..written]);
        }
        catch (DecoderFallbackException) { _invalid = true; }
        if (_invalid || !_filter.Finish() || _filter.Value is not Dictionary<string, object?> root ||
            !root.TryGetValue("payload", out var payload) || payload is not Dictionary<string, object?>)
            return ReadOnlyMemory<byte>.Empty;
        RemoveUnusedText(root);
        using (var writer = new Utf8JsonWriter(_output)) Write(writer, root);
        return _output.GetBuffer().AsMemory(0, (int)_output.Length);
    }

    public void Reset()
    {
        _decoder.Reset();
        _filter.Reset();
        _invalid = _completed = false;
        _output.SetLength(0);
        if (_output.Capacity > 128 * 1024) _output.Capacity = 0;
    }

    public void Dispose() { Reset(); _output.Dispose(); }

    private static void RemoveUnusedText(Dictionary<string, object?> root)
    {
        if (!root.TryGetValue("payload", out var value) || value is not Dictionary<string, object?> payload) return;
        var record = String(root, "type");
        var isUser = record == "event_msg" && string.Equals(String(payload, "type"), "user_message", StringComparison.OrdinalIgnoreCase) ||
                     record == "response_item" && String(payload, "type") == "message" && String(payload, "role") == "user";
        if (!isUser)
            foreach (var key in new[] { "text", "message", "content" }) payload.Remove(key);
        var name = String(payload, "name");
        if (record != "response_item" || name is null ||
            !new[] { "request_user_input", "request_user_input_async" }.Any(tool =>
                name == tool || name.EndsWith("." + tool, StringComparison.Ordinal) ||
                name.EndsWith("__" + tool, StringComparison.Ordinal)))
        {
            payload.Remove("arguments");
            payload.Remove("input");
        }
    }

    private static string? String(Dictionary<string, object?> node, string key) =>
        node.TryGetValue(key, out var value) ? value as string : null;

    private enum Kind { None, Root, Payload, Scalar, Text, Content, Part, Arguments, Questions,
        Output, Error, Usage, Info, Replies, Reply }
    private readonly record struct Number(string Text);
    private readonly record struct Questions(int Count);

    private static Kind Property(Kind parent, string? name) => parent switch
    {
        Kind.Root => name switch { "type" or "timestamp" => Kind.Scalar, "payload" => Kind.Payload, _ => Kind.None },
        Kind.Payload => name switch
        {
            "type" or "id" or "session_id" or "thread_source" or "cwd" or "model" or "effort" or
                "reasoning_effort" or "turn_id" or "role" or "name" or "call_id" or "status" or "reason" => Kind.Scalar,
            "text" or "message" => Kind.Text,
            "content" => Kind.Content,
            "arguments" or "input" => Kind.Arguments,
            "output" => Kind.Output,
            "usage" => Kind.Usage,
            "info" => Kind.Info,
            _ => Kind.None
        },
        Kind.Part => name switch { "type" => Kind.Scalar, "text" => Kind.Text, _ => Kind.None },
        Kind.Arguments => name == "questions" ? Kind.Questions : Kind.None,
        Kind.Output => name switch
        {
            "accepted" or "status" or "isError" or "cancelled" or "canceled" => Kind.Scalar,
            "error" => Kind.Error,
            _ => Kind.None
        },
        Kind.Usage => name == "total_tokens" ? Kind.Scalar : Kind.None,
        Kind.Info => name == "last_token_usage" ? Kind.Usage : Kind.None,
        Kind.Reply => name == "questionItemId" ? Kind.Scalar : Kind.None,
        _ => Kind.None
    };

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> fields:
                writer.WriteStartObject();
                foreach (var (name, child) in fields) { writer.WritePropertyName(name); Write(writer, child); }
                writer.WriteEndObject();
                break;
            case List<object?> items:
                writer.WriteStartArray();
                foreach (var item in items) Write(writer, item);
                writer.WriteEndArray();
                break;
            case Questions questions:
                writer.WriteStartArray();
                for (var index = 0; index < questions.Count; index++) writer.WriteNullValue();
                writer.WriteEndArray();
                break;
            case Number number: writer.WriteRawValue(number.Text); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            default: writer.WriteNullValue(); break;
        }
    }

    // A strict incremental lexer is necessary here: Utf8JsonReader requires a
    // complete string token, so retaining its unconsumed input retains the image.
    private sealed class JsonFilter(Kind rootKind)
    {
        private static readonly SearchValues<char> StringBoundaries = SearchValues.Create("\"\\");
        private static readonly SearchValues<char> UnescapedControls = SearchValues.Create(
            "\0\x01\x02\x03\x04\x05\x06\x07\x08\t\n\x0b\x0c\r\x0e\x0f" +
            "\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1a\x1b\x1c\x1d\x1e\x1f");
        private enum Stage { FirstKey, Key, Colon, Value, FirstItem, Item, AfterValue }
        private enum Token { None, Key, String, Primitive }
        private enum Numeric { Minus, Zero, Integer, Dot, Fraction, Exponent, Sign, ExponentDigits }
        private struct Frame
        {
            public bool Object;
            public Kind Kind;
            public Stage Stage;
            public string? Key;
            public Dictionary<string, object?>? Fields;
            public List<object?>? Items;
            public int Count;
        }

        private readonly List<Frame> _frames = new(4);
        private readonly StringBuilder _small = new(64);
        private Token _token;
        private Kind _valueKind;
        private IStringValue? _string;
        private bool _started, _done, _invalid, _escaped, _overlong, _retainKey;
        private int _unicodeDigits, _unicodeValue, _literalIndex;
        private string? _literal;
        private Numeric _numeric;
        public object? Value { get; private set; }
        public bool Done => _done;
        public bool Invalid => _invalid;
        public long RetainedBytes => !_started ? 0 :
            Estimate(Value) + _frames.Sum(frame => Estimate(frame.Fields) + Estimate(frame.Items) + 96) +
            _small.Capacity * 2L + (_string?.RetainedBytes ?? 0);

        public void Reset()
        {
            _frames.Clear(); _small.Clear(); _string = null; Value = null;
            _started = _done = _invalid = _escaped = _overlong = false;
            _unicodeDigits = _unicodeValue = _literalIndex = 0;
            _literal = null; _token = Token.None;
        }

        public void Feed(ReadOnlySpan<char> text)
        {
            while (!text.IsEmpty && !_invalid)
            {
                // The UTF8 decoder has already validated these characters. Skip
                // ordinary discarded runs in bulk, but still lex every escape and
                // reject unescaped controls rather than accepting malformed JSON.
                if (_token == Token.String && !_escaped && _unicodeDigits == 0 &&
                    (_valueKind == Kind.None && _string is null || _string is EmbeddedValue))
                {
                    var boundary = text.IndexOfAny(StringBoundaries);
                    var plain = boundary < 0 ? text : text[..boundary];
                    if (plain.IndexOfAny(UnescapedControls) >= 0) { _invalid = true; return; }
                    // Unescaped characters have the same representation inside
                    // encoded arguments/output JSON. Let its own lexer skip runs.
                    if (_string is EmbeddedValue { Discarding: false } embedded) embedded.Feed(plain);
                    text = text[plain.Length..];
                    if (text.IsEmpty) return;
                }
                Feed(text[0]);
                text = text[1..];
            }
        }

        public void Feed(char character)
        {
            if (_invalid) return;
            if (_token is Token.Key or Token.String) { StringCharacter(character); return; }
            if (_token == Token.Primitive)
            {
                if (!IsDelimiter(character)) { PrimitiveCharacter(character); return; }
                EndPrimitive();
                if (_invalid) return;
            }
            if (character is ' ' or '\t' or '\r' or '\n') return;
            if (!_started && character == '\uFEFF') { _started = true; return; }
            if (_done) { _invalid = true; return; }
            if (_frames.Count == 0)
            {
                if (_started && Value is not null) { _invalid = true; return; }
                _started = true; BeginValue(character, rootKind); return;
            }
            ref var frame = ref CollectionsMarshal.AsSpan(_frames)[^1];
            switch (frame.Stage)
            {
                case Stage.FirstKey:
                case Stage.Key:
                    if (character == '}' && frame.Stage == Stage.FirstKey) EndContainer(true);
                    else if (character == '"') BeginString(Kind.None, key: true);
                    else _invalid = true;
                    break;
                case Stage.Colon:
                    if (character == ':') frame.Stage = Stage.Value; else _invalid = true;
                    break;
                case Stage.FirstItem:
                    if (character == ']') { EndContainer(false); break; }
                    goto case Stage.Item;
                case Stage.Item:
                case Stage.Value:
                    var kind = frame.Object ? Property(frame.Kind, frame.Key) : frame.Kind switch
                    { Kind.Content => Kind.Part, Kind.Replies => Kind.Reply, _ => Kind.None };
                    frame.Stage = Stage.AfterValue;
                    frame.Count++;
                    BeginValue(character, kind);
                    break;
                case Stage.AfterValue:
                    if (character == ',') frame.Stage = frame.Object ? Stage.Key : Stage.Item;
                    else if (character == (frame.Object ? '}' : ']')) EndContainer(frame.Object);
                    else _invalid = true;
                    break;
            }
        }

        public bool Finish()
        {
            if (_token == Token.Primitive) EndPrimitive();
            return !_invalid && _done && _frames.Count == 0 && _token == Token.None;
        }

        private void BeginValue(char character, Kind kind)
        {
            _valueKind = kind;
            if (character is '{' or '[')
            {
                if (_frames.Count >= 256) { _invalid = true; return; }
                var isObject = character == '{';
                _frames.Add(new Frame
                {
                    Object = isObject, Kind = kind,
                    Stage = isObject ? Stage.FirstKey : Stage.FirstItem,
                    Fields = isObject && kind is Kind.Root or Kind.Payload or Kind.Part or Kind.Arguments or Kind.Output or
                        Kind.Usage or Kind.Info or Kind.Reply ? new(StringComparer.Ordinal) : null,
                    Items = !isObject && kind is Kind.Content or Kind.Replies ? new() : null
                });
            }
            else if (character == '"') BeginString(kind, key: false);
            else if (character is 't' or 'f' or 'n' or '-' or >= '0' and <= '9')
            {
                _token = Token.Primitive; _small.Clear(); _overlong = false;
                _literal = character switch { 't' => "true", 'f' => "false", 'n' => "null", _ => null };
                _literalIndex = 0;
                _numeric = character switch { '-' => Numeric.Minus, '0' => Numeric.Zero, _ => Numeric.Integer };
                if (_literal is not null) _literalIndex = 1;
                if (kind != Kind.None) _small.Append(character);
            }
            else _invalid = true;
        }

        private void EndContainer(bool isObject)
        {
            var frame = _frames[^1];
            if (frame.Object != isObject) { _invalid = true; return; }
            _frames.RemoveAt(_frames.Count - 1);
            object? value = frame.Kind == Kind.Error ? true : frame.Fields is not null ? frame.Fields :
                frame.Items is not null ? frame.Items : frame.Kind == Kind.Questions && !isObject ? new Questions(frame.Count) : null;
            Store(value);
        }

        private void Store(object? value)
        {
            if (_frames.Count == 0) { Value = value; _done = true; return; }
            ref var frame = ref CollectionsMarshal.AsSpan(_frames)[^1];
            if (frame.Fields is not null && frame.Key is not null && Property(frame.Kind, frame.Key) != Kind.None)
                frame.Fields[frame.Key] = value;
            else frame.Items?.Add(value);
        }

        private void BeginString(Kind kind, bool key)
        {
            _token = key ? Token.Key : Token.String;
            _valueKind = kind; _small.Clear(); _overlong = false;
            _retainKey = key && _frames.Count > 0 && _frames[^1].Fields is not null;
            _escaped = false; _unicodeDigits = 0;
            _string = kind switch
            {
                Kind.Text or Kind.Content => new TextValue(),
                Kind.Arguments or Kind.Output => new EmbeddedValue(kind),
                _ => null
            };
        }

        private void StringCharacter(char character)
        {
            if (_unicodeDigits > 0)
            {
                var digit = character is >= '0' and <= '9' ? character - '0' :
                    character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                    character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (digit < 0) { _invalid = true; return; }
                _unicodeValue = (_unicodeValue << 4) | digit;
                if (--_unicodeDigits == 0) Decoded((char)_unicodeValue);
                return;
            }
            if (_escaped)
            {
                _escaped = false;
                if (character == 'u') { _unicodeDigits = 4; _unicodeValue = 0; return; }
                switch (character)
                {
                    case '"': case '\\': case '/': Decoded(character); break;
                    case 'b': Decoded('\b'); break;
                    case 'f': Decoded('\f'); break;
                    case 'n': Decoded('\n'); break;
                    case 'r': Decoded('\r'); break;
                    case 't': Decoded('\t'); break;
                    default: _invalid = true; break;
                }
                return;
            }
            if (character == '\\') { _escaped = true; return; }
            if (character == '"')
            {
                if (_token == Token.Key)
                {
                    ref var frame = ref CollectionsMarshal.AsSpan(_frames)[^1];
                    frame.Key = _overlong || !_retainKey ? null : _small.ToString();
                    frame.Stage = Stage.Colon;
                }
                else Store(_valueKind == Kind.Error ? true : _string is not null ? _string.Finish() :
                    _valueKind == Kind.Scalar && !_overlong ? _small.ToString() : null);
                _token = Token.None; _string = null; _small.Clear();
                return;
            }
            if (character < ' ') { _invalid = true; return; }
            Decoded(character);
        }

        private void Decoded(char character)
        {
            if (_string is not null) { _string.Feed(character); return; }
            var limit = _token == Token.Key ? (_retainKey ? 128 : 0) : _valueKind == Kind.Scalar ? 4096 : 0;
            if (_small.Length < limit) _small.Append(character); else if (limit != 0) _overlong = true;
        }

        private void PrimitiveCharacter(char character)
        {
            if (_literal is not null)
            {
                if (_literalIndex >= _literal.Length || character != _literal[_literalIndex++]) _invalid = true;
            }
            else
            {
                var digit = character is >= '0' and <= '9';
                _numeric = _numeric switch
                {
                    Numeric.Minus when digit => character == '0' ? Numeric.Zero : Numeric.Integer,
                    Numeric.Integer when digit => Numeric.Integer,
                    Numeric.Zero or Numeric.Integer when character == '.' => Numeric.Dot,
                    Numeric.Zero or Numeric.Integer or Numeric.Fraction when character is 'e' or 'E' => Numeric.Exponent,
                    Numeric.Dot or Numeric.Fraction when digit => Numeric.Fraction,
                    Numeric.Exponent when character is '+' or '-' => Numeric.Sign,
                    Numeric.Exponent or Numeric.Sign or Numeric.ExponentDigits when digit => Numeric.ExponentDigits,
                    _ => (Numeric)(-1)
                };
                if ((int)_numeric < 0) _invalid = true;
            }
            if (_valueKind != Kind.None)
            {
                if (_small.Length < 96) _small.Append(character); else _overlong = true;
            }
        }

        private void EndPrimitive()
        {
            if (_literal is not null ? _literalIndex != _literal.Length :
                _numeric is Numeric.Minus or Numeric.Dot or Numeric.Exponent or Numeric.Sign || (int)_numeric < 0)
                _invalid = true;
            object? value = _valueKind == Kind.None ? null : _literal switch
            {
                "null" => null,
                _ when _valueKind == Kind.Error => true,
                "true" => true,
                "false" => false,
                _ when !_overlong => new Number(_small.ToString()),
                _ => null
            };
            _token = Token.None; _small.Clear();
            if (!_invalid) Store(value);
        }

        private static bool IsDelimiter(char character) => character is ' ' or '\t' or '\r' or '\n' or ',' or ']' or '}';
        private static long Estimate(object? value) => value switch
        {
            string text => text.Length * 2L,
            Dictionary<string, object?> fields => 128 + fields.Sum(item => 64 + item.Key.Length * 2L + Estimate(item.Value)),
            List<object?> items => 64 + items.Sum(item => 16 + Estimate(item)),
            _ => 0
        };
    }

    private interface IStringValue
    {
        void Feed(char character);
        object? Finish();
        long RetainedBytes { get; }
    }

    private sealed class EmbeddedValue(Kind kind) : IStringValue
    {
        private readonly JsonFilter _filter = new(kind);
        public bool Discarding => _filter.Invalid;
        public long RetainedBytes => _filter.RetainedBytes;
        public void Feed(char character) => _filter.Feed(character);
        public void Feed(ReadOnlySpan<char> characters) => _filter.Feed(characters);
        public object? Finish() => _filter.Finish() ? _filter.Value : null;
    }

    private sealed class TextValue : IStringValue
    {
        private const string Request = "## My request:";
        private const string Reply = "<send_user_message_question_reply>";
        private const string ReplyEnd = "</send_user_message_question_reply>";
        private readonly WindowText _text = new();
        private WindowText? _request;
        private JsonFilter? _reply;
        private StringBuilder? _suffix;
        private int _requestMatch, _replyMatch;
        private bool _replyImpossible;
        public long RetainedBytes => _text.RetainedBytes + (_request?.RetainedBytes ?? 0) +
            (_reply?.RetainedBytes ?? 0) + (_suffix?.Capacity * 2L ?? 0);

        public void Feed(char character)
        {
            if (_reply is not null)
            {
                if (_reply.Done)
                {
                    _suffix ??= new StringBuilder();
                    if (_suffix.Length < 128) _suffix.Append(character); else _replyImpossible = true;
                }
                else _reply.Feed(character);
                return;
            }
            _text.Feed(character);
            _request?.Feed(character);
            while (_requestMatch > 0 && char.ToUpperInvariant(character) != char.ToUpperInvariant(Request[_requestMatch]))
                _requestMatch = _requestMatch == 2 ? 1 : 0;
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(Request[_requestMatch])) _requestMatch++;
            if (_requestMatch == Request.Length)
            {
                _request ??= new WindowText(); _request.Reset();
                foreach (var item in Request) _request.Feed(item);
                _requestMatch = 0;
            }
            if (_replyImpossible) return;
            if (_replyMatch == 0 && char.IsWhiteSpace(character)) return;
            if (character != Reply[_replyMatch]) { _replyImpossible = true; return; }
            if (++_replyMatch == Reply.Length) _reply = new JsonFilter(Kind.Replies);
        }

        public object? Finish()
        {
            if (_reply is null) return (_request ?? _text).Finish();
            var suffix = _suffix?.ToString().Trim();
            if (_replyImpossible || !_reply.Finish() || suffix is not (null or "") && suffix != ReplyEnd)
                return Reply; // An invalid reply must not resolve any pending request.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Write(writer, _reply.Value);
            return Reply + " " + Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
    }

    // Keep meaningful beginning/end text, even if a request marker occurs in the
    // middle of a very large context. The ellipsis makes truncation explicit.
    private sealed class WindowText
    {
        private const int Limit = 16 * 1024;
        private readonly StringBuilder _prefix = new(256);
        private char[]? _tail;
        private int _tailCount, _tailPosition;
        public long RetainedBytes => _prefix.Capacity * 2L + (_tail?.Length * 2L ?? 0);
        public void Reset() { _prefix.Clear(); _tailCount = _tailPosition = 0; }
        public void Feed(char character)
        {
            if (_prefix.Length < Limit) { _prefix.Append(character); return; }
            _tail ??= new char[Limit];
            _tail[_tailPosition] = character;
            _tailPosition = (_tailPosition + 1) % Limit;
            if (_tailCount < Limit) _tailCount++;
        }
        public string Finish()
        {
            if (_tailCount == 0) return _prefix.ToString();
            var result = new StringBuilder(_prefix.Length + _tailCount + 3).Append(_prefix);
            if (_tailCount == Limit) result.Append(" … ");
            var start = _tailCount == Limit ? _tailPosition : 0;
            result.Append(_tail!, start, _tailCount - start);
            if (start > 0) result.Append(_tail!, 0, start);
            return result.ToString();
        }
    }
}
