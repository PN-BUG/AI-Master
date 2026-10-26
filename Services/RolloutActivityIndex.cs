using System.Globalization;
using System.Text.Json;

namespace AIMaster.Services;

/// <summary>Indexes readable rollout activity independently of delayed Windows write timestamps.</summary>
internal sealed class RolloutActivityIndex
{
    private const int TailBytes = 256 * 1024;
    private const int HeaderBytes = 64 * 1024;
    private readonly object _sync = new();
    private readonly byte[] _buffer = new byte[TailBytes];
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record Candidate(string Path, long Length, DateTime ActivityAtUtc, bool IsUserSession = true);

    private sealed record Entry(Candidate Candidate, DateTime LastWriteAtUtc,
        DateTime? RecordActivityAtUtc, bool HeaderKnown);

    public IReadOnlyList<Candidate> GetCandidates(string sessionsRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(sessionsRoot)) return Array.Empty<Candidate>();
            var candidates = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enumerated = false;
            try
            {
                // Do not cap by filesystem mtime: an open writer can append for hours while
                // Windows continues reporting the timestamp from its previous closed handle.
                foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", new EnumerationOptions
                         { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seen.Add(path);
                    try
                    {
                        var entry = ReadEntry(path, cancellationToken);
                        _entries[path] = entry;
                        candidates.Add(entry.Candidate);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A rollout can move or disappear between enumeration and opening it.
                    }
                }
                enumerated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retain the cache if a directory temporarily disappears during enumeration.
            }

            if (enumerated)
                foreach (var path in _entries.Keys.Where(path => !seen.Contains(path)).ToArray())
                    _entries.Remove(path);
            return candidates.OrderByDescending(item => item.ActivityAtUtc)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private Entry ReadEntry(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        var length = stream.Length;
        var lastWriteAtUtc = File.GetLastWriteTimeUtc(path);
        _entries.TryGetValue(path, out var previous);
        if (previous is not null && previous.Candidate.Length == length && previous.LastWriteAtUtc == lastWriteAtUtc)
            return previous;

        var isAppend = previous is not null && length > previous.Candidate.Length;
        var headerKnown = previous?.HeaderKnown ?? false;
        var isUserSession = previous?.Candidate.IsUserSession ?? true;
        if (previous is null || !headerKnown || length < previous.Candidate.Length ||
            previous.LastWriteAtUtc != lastWriteAtUtc)
            (isUserSession, headerKnown) = ReadSessionHeader(stream, length, cancellationToken);

        var recordActivityAtUtc = ReadLatestRecordTimestamp(stream, length, cancellationToken);
        if (!recordActivityAtUtc.HasValue && isAppend)
            recordActivityAtUtc = previous!.RecordActivityAtUtc;
        if (!recordActivityAtUtc.HasValue)
            recordActivityAtUtc = ReadTimestampBeyondTail(stream, length, cancellationToken);
        return new Entry(new Candidate(path, length, recordActivityAtUtc ?? lastWriteAtUtc, isUserSession),
            lastWriteAtUtc, recordActivityAtUtc, headerKnown);
    }

    private (bool IsUserSession, bool HeaderKnown) ReadSessionHeader(FileStream stream, long length,
        CancellationToken cancellationToken)
    {
        var read = ReadWindow(stream, 0, (int)Math.Min(length, HeaderBytes), cancellationToken);
        var start = 0;
        for (var end = 0; end < read; end++)
        {
            if (_buffer[end] != (byte)'\n') continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(WithoutBom(_buffer.AsMemory(start, end - start)),
                    new JsonDocumentOptions { MaxDepth = 256 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && ReadString(root, "type") == "session_meta")
                {
                    var source = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                        ? ReadString(payload, "thread_source")
                        : null;
                    return (string.IsNullOrWhiteSpace(source) || string.Equals(source, "user", StringComparison.OrdinalIgnoreCase), true);
                }
            }
            catch (JsonException) { }
            start = end + 1;
        }
        return (true, false);
    }

    private DateTime? ReadLatestRecordTimestamp(FileStream stream, long length, CancellationToken cancellationToken)
    {
        var offset = Math.Max(0, length - TailBytes);
        var startsAtRecord = offset == 0;
        if (!startsAtRecord)
        {
            stream.Position = offset - 1;
            startsAtRecord = stream.ReadByte() == (byte)'\n';
        }
        var read = ReadWindow(stream, offset, (int)Math.Min(length, TailBytes), cancellationToken);
        var end = read - 1;
        // An unterminated final line may still be in the middle of a writer append.
        while (end >= 0 && _buffer[end] != (byte)'\n') end--;
        while (end >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = end - 1;
            while (before >= 0 && _buffer[before] != (byte)'\n') before--;
            var start = before + 1;
            if (start == 0 && !startsAtRecord) break;
            try
            {
                using var document = JsonDocument.Parse(WithoutBom(_buffer.AsMemory(start, end - start)),
                    new JsonDocumentOptions { MaxDepth = 256 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    DateTimeOffset.TryParse(ReadString(root, "timestamp"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                    return timestamp.UtcDateTime;
            }
            catch (JsonException) { }
            end = before;
        }
        return null;
    }

    private DateTime? ReadTimestampBeyondTail(FileStream stream, long length, CancellationToken cancellationToken)
    {
        // A cold index has no cached timestamp to retain when one record fills the tail.
        // Locate complete JSONL boundaries backwards in fixed-size windows instead of
        // allocating the full record or treating its stale filesystem mtime as activity.
        var end = FindPreviousNewline(stream, length, cancellationToken);
        while (end >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = FindPreviousNewline(stream, end, cancellationToken);
            var start = before + 1;
            var recordLength = end - start;
            var read = ReadWindow(stream, start, (int)Math.Min(recordLength, TailBytes), cancellationToken);
            var bytes = WithoutBom(_buffer.AsMemory(0, read));
            try
            {
                if (recordLength <= TailBytes)
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 256 });
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        DateTimeOffset.TryParse(ReadString(root, "timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                        return timestamp.UtcDateTime;
                }
                else
                {
                    // Real rollout records put timestamp in their root prefix. A non-final
                    // reader can consume it before a huge text token, without copying that
                    // token or confusing a nested/prose timestamp with the root property.
                    var reader = new Utf8JsonReader(bytes.Span, isFinalBlock: false,
                        new JsonReaderState(new JsonReaderOptions { MaxDepth = 256 }));
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                                !reader.ValueTextEquals("timestamp")) continue;
                            if (!reader.Read()) break;
                            if (reader.TokenType == JsonTokenType.String &&
                                DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                                return timestamp.UtcDateTime;
                        }
                }
            }
            catch (JsonException) { }
            end = before;
        }
        return null;
    }

    private long FindPreviousNewline(FileStream stream, long before, CancellationToken cancellationToken)
    {
        while (before > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Math.Max(0, before - TailBytes);
            var read = ReadWindow(stream, start, (int)(before - start), cancellationToken);
            for (var index = read - 1; index >= 0; index--)
                if (_buffer[index] == (byte)'\n') return start + index;
            before = start;
        }
        return -1;
    }

    private int ReadWindow(FileStream stream, long offset, int count, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var read = 0;
        while (read < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stream.Read(_buffer, read, count - read);
            if (current == 0) break;
            read += current;
        }
        return read;
    }

    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> line) =>
        line.Length >= 3 && line.Span[0] == 0xef && line.Span[1] == 0xbb && line.Span[2] == 0xbf
            ? line[3..]
            : line;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
