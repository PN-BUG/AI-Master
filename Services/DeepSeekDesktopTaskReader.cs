using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIMaster.Models;

namespace AIMaster.Services;

internal static class DeepSeekDesktopPaths
{
    /// <summary>Profile root of the DeepSeek Harness desktop app; overridable for tests.</summary>
    public static string GetHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_DESKTOP_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "@deepseek-ai", "dsh-desktop")
            : configured;
    }

    public static string GetLevelDbDirectory(string? home = null) =>
        Path.Combine(string.IsNullOrWhiteSpace(home) ? GetHome() : home, "Local Storage", "leveldb");
}

/// <summary>
/// Reads the conversations the DeepSeek Harness desktop app keeps in its Chromium LocalStorage
/// LevelDB. The app keeps its conversation history in the cloud, so locally only the session ids
/// and the prompt text the user is typing survive; tasks therefore name themselves after the first
/// prompt and carry no model, token or project data. Only local files are read, with a shared lock
/// so a running app may keep writing; nothing is written or started.
/// </summary>
internal sealed class DeepSeekDesktopTaskReader
{
    private const string ConversationKey = "dsh.conversation.session-";
    private const string QuestionKey = "dsh.user-questions.drafts.v1.session-";
    private const string CurrentSessionKey = "dsh.sessions.current";
    private static readonly Regex SessionIdPattern = new(
        @"session-([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _levelDbDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _cacheWindow;
    private readonly object _sync = new();
    private List<AiThreadSummary>? _cached;
    private DateTimeOffset _cachedAt;

    public DeepSeekDesktopTaskReader(string? levelDbDirectory = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? cacheWindow = null)
    {
        _levelDbDirectory = levelDbDirectory ?? DeepSeekDesktopPaths.GetLevelDbDirectory();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _cacheWindow = cacheWindow ?? TimeSpan.FromSeconds(1);
    }

    public string LevelDbDirectory => _levelDbDirectory;

    public IReadOnlyList<AiThreadSummary> Read(int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock();
        List<AiThreadSummary>? cached;
        lock (_sync)
        {
            cached = _cached is not null && now - _cachedAt < _cacheWindow ? _cached : null;
        }
        if (cached is not null) return Limit(cached, limit);

        var tasks = BuildTasks(ReadEntries(cancellationToken));
        lock (_sync)
        {
            _cached = tasks;
            _cachedAt = now;
        }
        return Limit(tasks, limit);
    }

    private static IReadOnlyList<AiThreadSummary> Limit(List<AiThreadSummary> tasks, int limit) =>
        tasks.Count <= Math.Max(1, limit)
            ? tasks
            : tasks.Take(Math.Max(1, limit)).ToList();

    /// <summary>
    /// A conversation leaves two kinds of records: the prompt being typed, which resets whenever a
    /// message is sent, and the pending questions the app asks while it works. The last prompt
    /// before a reset is a completed message, so the first one names the conversation the way chat
    /// apps usually do and the last one is the latest thing the user sent.
    /// </summary>
    private static List<AiThreadSummary> BuildTasks(List<DesktopEntry> entries)
    {
        var sessions = new Dictionary<string, SessionBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var builder = GetSession(sessions, entry.SessionId);
            switch (entry.Kind)
            {
                case DesktopEntryKind.Prompt:
                    builder.AddPrompt(entry.Text, entry.Timestamp);
                    break;
                case DesktopEntryKind.Questions:
                    builder.AddQuestions(entry.Text, entry.Timestamp);
                    break;
            }
        }

        return AiManagerService.SortTasksByStatusAndTime(
                sessions.Values.Where(builder => builder.HasContent).Select(builder => builder.Build()))
            .ToList();

        static SessionBuilder GetSession(Dictionary<string, SessionBuilder> sessions, string id)
        {
            if (!sessions.TryGetValue(id, out var builder))
                sessions[id] = builder = new SessionBuilder(id);
            return builder;
        }
    }

    private List<DesktopEntry> ReadEntries(CancellationToken cancellationToken)
    {
        var result = new List<DesktopEntry>();
        if (!Directory.Exists(_levelDbDirectory)) return result;
        string[] files;
        try
        {
            files = Directory.GetFiles(_levelDbDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        // Compacted tables hold older writes and current logs newer ones; newest write wins.
        var ordered = files
            .Where(path => path.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
        foreach (var path in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<LevelDbRecord> records;
            try
            {
                records = path.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase)
                    ? LevelDbTableReader.Read(path, cancellationToken)
                    : LevelDbLogReader.Read(path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var record in records)
            {
                if (TryDescribe(record.Key, record.Value, record.Timestamp) is { } entry)
                    result.Add(entry);
            }
        }
        return result;
    }

    /// <summary>Classifies one LevelDB record and decodes the parts this reader uses.</summary>
    internal static DesktopEntry? TryDescribe(string key, byte[] value, DateTimeOffset timestamp)
    {
        if (key.Contains(CurrentSessionKey, StringComparison.Ordinal))
        {
            var sessionId = ReadJsonString(value, "sessionId");
            return string.IsNullOrWhiteSpace(sessionId) ? null
                : new DesktopEntry(DesktopEntryKind.CurrentSession, sessionId.Trim(), null, timestamp);
        }

        if (TrySessionId(key, QuestionKey, out var questionSession))
            return new DesktopEntry(DesktopEntryKind.Questions, questionSession, DecodeValue(value), timestamp);

        if (TrySessionId(key, ConversationKey, out var conversationSession))
            return new DesktopEntry(DesktopEntryKind.Prompt, conversationSession, DecodeValue(value), timestamp);

        return null;
    }

    private static bool TrySessionId(string key, string prefix, out string sessionId)
    {
        sessionId = string.Empty;
        var index = key.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0) return false;
        var match = SessionIdPattern.Match(key, index);
        if (!match.Success) return false;
        sessionId = "session-" + match.Groups[1].Value;
        return true;
    }

    /// <summary>
    /// Chromium writes LocalStorage values as UTF-16 code units behind an alignment byte, while
    /// other builds write UTF-8; accept whichever reading starts like the JSON this reader expects.
    /// </summary>
    internal static string? DecodeValue(byte[] value)
    {
        for (var start = 0; start <= 2 && start < value.Length; start++)
        {
            var text = Encoding.Unicode.GetString(value, start, value.Length - start);
            if (text.StartsWith('{') || text.StartsWith('[')) return text;
        }
        var utf8 = Encoding.UTF8.GetString(value);
        return utf8.StartsWith('{') || utf8.StartsWith('[') ? utf8 : null;
    }

    private static string? ReadJsonString(byte[] value, string property)
    {
        var text = DecodeValue(value);
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(property, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal enum DesktopEntryKind
    {
        Prompt,
        Questions,
        CurrentSession
    }

    internal sealed record DesktopEntry(DesktopEntryKind Kind, string SessionId,
        string? Text, DateTimeOffset Timestamp);

    private sealed class SessionBuilder(string id)
    {
        private string? _openPrompt;
        private string? _firstPrompt;
        private string? _latestPrompt;
        private bool _waitingForInput;
        private DateTimeOffset _updatedAt;

        public void AddPrompt(string? value, DateTimeOffset timestamp)        {
            if (timestamp > _updatedAt) _updatedAt = timestamp;
            var text = ReadPromptText(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                // A reset closes the prompt that was being typed; that text is a sent message.
                if (_openPrompt is null) return;
                _firstPrompt ??= _openPrompt;
                _latestPrompt = _openPrompt;
                _openPrompt = null;
                return;
            }
            _openPrompt = text;
        }

        public void AddQuestions(string? value, DateTimeOffset timestamp)
        {
            if (timestamp > _updatedAt) _updatedAt = timestamp;
            if (value is null) return;
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;
                if (!document.RootElement.TryGetProperty("progressByRequest", out var progress)) return;
                if (progress.ValueKind == JsonValueKind.Object && progress.EnumerateObject().Any())
                    _waitingForInput = true;
            }
            catch (JsonException) { }
        }

        public bool HasContent => !string.IsNullOrWhiteSpace(_openPrompt) ||
                                  !string.IsNullOrWhiteSpace(_firstPrompt) ||
                                  !string.IsNullOrWhiteSpace(_latestPrompt);

        public AiThreadSummary Build()
        {
            if (_openPrompt is not null)
            {
                _firstPrompt ??= _openPrompt;
                _latestPrompt = _openPrompt;
            }
            return new AiThreadSummary
            {
                Id = id,
                Title = TaskTitleFormatter.Clean(_firstPrompt) is { Length: > 0 } first ? first : "未命名任务",
                ConversationTitle = _firstPrompt,
                LatestUserMessage = _latestPrompt,
                Status = _waitingForInput ? "waitingOnUserInput" : "completed",
                Source = AiProviderModes.DeepSeek,
                // The app keeps its model choice in the cloud; nothing local names the model.
                OpenUri = "dsh://open",
                UpdatedAt = _updatedAt == default ? null : _updatedAt
            };
        }

        private static string? ReadPromptText(string? value)
        {
            if (value is null) return null;
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("draft", out var draft) &&
                       draft.ValueKind == JsonValueKind.Object &&
                       draft.TryGetProperty("text", out var text) &&
                       text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>One key/value record read from a LevelDB store, with its estimated write time.</summary>
internal sealed record LevelDbRecord(string Key, byte[] Value, DateTimeOffset Timestamp);

/// <summary>
/// Reads LevelDB write-ahead logs (<c>*.log</c>): 32 KiB blocks of records carrying WriteBatch
/// payloads. A running app appends while this reader walks the file, so a truncated tail simply
/// ends the scan.
/// </summary>
internal static class LevelDbLogReader
{
    private const int BlockSize = 32 * 1024;
    private const int HeaderSize = 7;

    public static List<LevelDbRecord> Read(string path, CancellationToken cancellationToken = default)
    {
        var bytes = ReadShared(path);
        if (bytes is null) return new List<LevelDbRecord>();
        var created = File.GetCreationTimeUtc(path);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var span = lastWrite > created ? lastWrite - created : TimeSpan.Zero;
        var records = new List<LevelDbRecord>();

        var offset = 0;
        var fragment = new List<byte>();
        var assembling = false;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockRemaining = BlockSize - offset % BlockSize;
            if (blockRemaining < HeaderSize)
            {
                offset += blockRemaining;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2));
            var type = bytes[offset + 6];
            offset += HeaderSize;
            if (length > bytes.Length - offset) break; // torn tail from a concurrent writer
            var data = bytes.AsSpan(offset, length).ToArray();
            offset += length;

            byte[] payload;
            switch (type)
            {
                case 1: // full record
                    if (assembling) { fragment.Clear(); assembling = false; }
                    payload = data;
                    break;
                case 2: // first fragment
                    fragment.Clear();
                    fragment.AddRange(data);
                    assembling = true;
                    continue;
                case 3: // middle fragment
                    if (!assembling) continue;
                    fragment.AddRange(data);
                    continue;
                case 4: // last fragment
                    if (!assembling) continue;
                    fragment.AddRange(data);
                    payload = fragment.ToArray();
                    fragment.Clear();
                    assembling = false;
                    break;
                default:
                    continue;
            }

            foreach (var record in ParseBatch(payload, TimestampAt(offset, bytes.Length, created, span)))
                records.Add(record);
        }
        return records;
    }

    /// <summary>The log is append-only, so a record's byte offset orders it between creation and the
    /// last write; interpolating between them is the best available estimate of when it was saved.</summary>
    private static DateTimeOffset TimestampAt(int endOffset, int length, DateTime created, TimeSpan span)
    {
        if (length <= 0 || span <= TimeSpan.Zero) return new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc));
        var fraction = Math.Clamp((double)endOffset / length, 0d, 1d);
        return new DateTimeOffset(created.AddTicks((long)(span.Ticks * fraction)));
    }

    /// <summary>Decodes a WriteBatch: an 8-byte sequence, a 4-byte count, then tagged key/value pairs.</summary>
    internal static List<LevelDbRecord> ParseBatch(byte[] payload, DateTimeOffset timestamp)
    {
        var records = new List<LevelDbRecord>();
        if (payload.Length < 12) return records;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
        var position = 12;
        for (var index = 0; index < count && position < payload.Length; index++)
        {
            var tag = payload[position++];
            var key = ReadLengthPrefixed(payload, ref position);
            if (key is null) break;
            if (tag != 1) continue; // deletions carry no value
            var value = ReadLengthPrefixed(payload, ref position);
            if (value is null) break;
            records.Add(new LevelDbRecord(Encoding.UTF8.GetString(key), value, timestamp));
        }
        return records;
    }

    private static byte[]? ReadLengthPrefixed(byte[] payload, ref int position)
    {
        var length = 0;
        var shift = 0;
        while (position < payload.Length)
        {
            var next = payload[position++];
            length |= (next & 0x7f) << shift;
            if ((next & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) return null;
        }
        if (length < 0 || length > payload.Length - position) return null;
        var value = payload.AsSpan(position, length).ToArray();
        position += length;
        return value;
    }

    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count <= 0) break;
                read += count;
            }
            return read == bytes.Length ? bytes : bytes.AsSpan(0, read).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads LevelDB table files (<c>*.ldb</c>): a footer pointing at an index block whose entries
/// point at data blocks of prefix-compressed key/value pairs, each optionally Snappy-compressed.
/// </summary>
internal static class LevelDbTableReader
{
    private const int FooterSize = 48;
    private const int BlockTrailerSize = 5;

    public static List<LevelDbRecord> Read(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count <= 0) break;
                read += count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<LevelDbRecord>();
        }
        if (bytes.Length < FooterSize) return new List<LevelDbRecord>();

        // Compaction rewrote these keys once, so every entry is as old as the table itself.
        var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        var footer = bytes.AsSpan(bytes.Length - FooterSize, FooterSize);
        // The footer ends with two handles: the metaindex block, then the index block.
        var metaIndexLength = ReadVarintLength(footer);
        var indexStart = metaIndexLength + (metaIndexLength < footer.Length
            ? ReadVarintLength(footer.Slice(metaIndexLength))
            : 0);
        if (indexStart <= 0 ||
            !TryReadHandle(footer.Slice(indexStart), out var indexOffset, out var indexSize))
            return new List<LevelDbRecord>();

        var index = ReadBlock(bytes, indexOffset, indexSize);
        if (index is null) return new List<LevelDbRecord>();

        var records = new List<LevelDbRecord>();
        foreach (var entry in index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = entry.Value.AsSpan();
            if (!TryReadHandle(handle, out var blockOffset, out var blockSize)) continue;
            var block = ReadBlock(bytes, blockOffset, blockSize);
            if (block is null) continue;
            foreach (var pair in block)
                records.Add(new LevelDbRecord(pair.Key, pair.Value, timestamp));
        }
        return records;
    }

    /// <summary>Reads one block's trailer to learn its compression, then decodes its entries.</summary>
    private static List<KeyValuePair<string, byte[]>>? ReadBlock(byte[] bytes, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset + size + BlockTrailerSize > bytes.Length) return null;
        var compression = bytes[offset + size];
        var contents = bytes.AsSpan(offset, size).ToArray();
        if (compression == 1)
        {
            try
            {
                contents = SnappyCodec.Decompress(contents);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
        else if (compression != 0)
        {
            return null;
        }
        return ParseBlock(contents);
    }

    /// <summary>Decodes a data block: shared-prefix entries, then the restart array footer.</summary>
    internal static List<KeyValuePair<string, byte[]>> ParseBlock(byte[] contents)
    {
        var entries = new List<KeyValuePair<string, byte[]>>();
        if (contents.Length < 4) return entries;
        var restarts = BinaryPrimitives.ReadInt32LittleEndian(contents.AsSpan(contents.Length - 4, 4));
        if (restarts < 0 || restarts > (contents.Length - 4) / 4) return entries;
        var end = contents.Length - 4 - restarts * 4;

        var position = 0;
        var previous = Array.Empty<byte>();
        while (position < end)
        {
            var shared = (int)ReadVarint(contents, ref position);
            var nonShared = (int)ReadVarint(contents, ref position);
            var valueLength = (int)ReadVarint(contents, ref position);
            if (shared < 0 || nonShared < 0 || valueLength < 0) break;
            if (position + nonShared + valueLength > end) break;

            var key = new byte[shared + nonShared];
            Array.Copy(previous, key, Math.Min(shared, previous.Length));
            Array.Copy(contents, position, key, shared, nonShared);
            position += nonShared;
            var value = contents.AsSpan(position, valueLength).ToArray();
            position += valueLength;
            previous = key;
            entries.Add(new KeyValuePair<string, byte[]>(Encoding.UTF8.GetString(key), value));
        }
        return entries;
    }

    /// <summary>BlockHandle: an offset and a size, both varints.</summary>
    private static bool TryReadHandle(ReadOnlySpan<byte> buffer, out int offset, out int size)
    {
        offset = 0;
        size = 0;
        var position = 0;
        var readOffset = ReadVarint(buffer, ref position);
        var readSize = ReadVarint(buffer, ref position);
        if (readOffset is < 0 or > int.MaxValue || readSize is < 0 or > int.MaxValue) return false;
        offset = (int)readOffset;
        size = (int)readSize;
        return true;
    }

    private static int ReadVarintLength(ReadOnlySpan<byte> buffer)
    {
        var position = 0;
        ReadVarint(buffer, ref position);
        return position;
    }

    private static long ReadVarint(ReadOnlySpan<byte> buffer, ref int position)
    {
        long result = 0;
        var shift = 0;
        while (position < buffer.Length)
        {
            var next = buffer[position++];
            result |= (long)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) return result;
        }
        return result;
    }
}

/// <summary>
/// Minimal decoder for the raw Snappy block format LevelDB uses: an uncompressed length followed by
/// literal runs and back-reference copies.
/// </summary>
internal static class SnappyCodec
{
    public static byte[] Decompress(byte[] input)
    {
        var position = 0;
        var expected = ReadVarint(input, ref position);
        if (expected is < 0 or > int.MaxValue) throw new InvalidDataException("Snappy length out of range.");
        var output = new byte[(int)expected];
        var written = 0;

        while (position < input.Length)
        {
            var tag = input[position++];
            switch (tag & 0x03)
            {
                case 0: // literal
                {
                    var length = tag >> 2;
                    if (length >= 60)
                    {
                        var extra = length - 59;
                        if (position + extra > input.Length) throw new InvalidDataException("Truncated literal length.");
                        length = 0;
                        for (var index = 0; index < extra; index++)
                            length |= input[position++] << (index * 8);
                    }
                    length++;
                    if (position + length > input.Length) throw new InvalidDataException("Truncated literal.");
                    if (written + length > output.Length) throw new InvalidDataException("Literal overruns output.");
                    Array.Copy(input, position, output, written, length);
                    position += length;
                    written += length;
                    break;
                }
                default: // back-reference copies
                {
                    int length;
                    int offset;
                    switch (tag & 0x03)
                    {
                        case 1:
                            length = 4 + ((tag >> 2) & 0x07);
                            if (position >= input.Length) throw new InvalidDataException("Truncated copy offset.");
                            offset = ((tag & 0xe0) << 3) | input[position++];
                            break;
                        case 2:
                            length = 1 + (tag >> 2);
                            if (position + 2 > input.Length) throw new InvalidDataException("Truncated copy offset.");
                            offset = input[position] | (input[position + 1] << 8);
                            position += 2;
                            break;
                        default:
                            length = 1 + (tag >> 2);
                            if (position + 4 > input.Length) throw new InvalidDataException("Truncated copy offset.");
                            offset = input[position] | (input[position + 1] << 8) |
                                     (input[position + 2] << 16) | (input[position + 3] << 24);
                            position += 4;
                            break;
                    }
                    if (offset <= 0 || offset > written) throw new InvalidDataException("Copy offset before output.");
                    if (written + length > output.Length) throw new InvalidDataException("Copy overruns output.");
                    for (var index = 0; index < length; index++)
                        output[written + index] = output[written + index - offset];
                    written += length;
                    break;
                }
            }
        }
        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }

    private static int ReadVarint(byte[] input, ref int position)
    {
        int result = 0;
        var shift = 0;
        while (position < input.Length)
        {
            var next = input[position++];
            result |= (next & 0x7f) << shift;
            if ((next & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("Snappy length varint is too long.");
        }
        throw new InvalidDataException("Truncated Snappy length.");
    }
}
