using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDeepSeekDesktopSupport()
    {
        VerifyDeepSeekDesktopLogReader();
        VerifySnappyCodec();
        VerifyLevelDbTableReader();
        VerifyDeepSeekDesktopMergesIntoFeed();
        VerifyDeepSeekDesktopActivation();
    }

    /// <summary>Clicking a desktop conversation must reach the app without relying on the protocol
    /// handler, so the window probe has to be reliable and safe when the app is not running.</summary>
    private static void VerifyDeepSeekDesktopActivation()
    {
        var found = DeepSeekDesktopApp.TryGetWindow(out var handle);
        Check(found == (handle != IntPtr.Zero) &&
              DeepSeekDesktopApp.OpenUri == "dsh://open" &&
              DeepSeekDesktopApp.ProcessName == "DeepSeek Harness",
            "probing the DeepSeek desktop window reports a consistent handle and targets the documented deep link");
    }

    /// <summary>Chromium keeps the desktop app's conversations in a LevelDB log; this builds the
    /// smallest store that exercises the reader: prompts typed and reset, plus pending questions.</summary>
    private static void VerifyDeepSeekDesktopLogReader()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-dsh-desktop-" + Guid.NewGuid().ToString("N"));
        var levelDb = Path.Combine(root, "Local Storage", "leveldb");
        Directory.CreateDirectory(levelDb);
        try
        {
            var typed = DesktopSessionPrefix("11111111-1111-1111-1111-111111111111");
            var questions = DesktopSessionPrefix("11111111-1111-1111-1111-111111111111");
            var openOnly = DesktopSessionPrefix("22222222-2222-2222-2222-222222222222");
            WriteLevelDbLog(Path.Combine(levelDb, "000003.log"),
            [
                // The first prompt was sent, so it names the conversation; the second was sent too.
                (typed + "dsh.conversation.session-11111111-1111-1111-1111-111111111111",
                    DesktopValue("""{"draft":{"text":"优化仪表盘布局","references":[]},"view":null}""")),
                (typed + "dsh.conversation.session-11111111-1111-1111-1111-111111111111",
                    DesktopValue("""{"draft":{"text":"","references":[]},"view":null}""")),
                (typed + "dsh.conversation.session-11111111-1111-1111-1111-111111111111",
                    DesktopValue("""{"draft":{"text":"新增配额卡片","references":[]},"view":null}""")),
                (typed + "dsh.conversation.session-11111111-1111-1111-1111-111111111111",
                    DesktopValue("""{"draft":{"text":"","references":[]},"view":null}""")),
                // A session whose prompt is still being typed is a single, still-open conversation.
                (openOnly + "dsh.conversation.session-22222222-2222-2222-2222-222222222222",
                    DesktopValue("""{"draft":{"text":"统一任务列表","references":[]},"view":null}""")),
                // The app recorded an unanswered question, so that conversation is waiting for input.
                (questions + "dsh.user-questions.drafts.v1.session-11111111-1111-1111-1111-111111111111",
                    DesktopValue("""{"progressByRequest":{"question:abc:1":{"index":0,"drafts":[],"wait":"editing"}}}"""))
            ]);

            var reader = new DeepSeekDesktopTaskReader(levelDb,
                () => DateTimeOffset.Parse("2026-11-13T18:00:00+08:00"), TimeSpan.Zero);
            var tasks = reader.Read(60);
            Check(tasks.Count == 2 &&
                  tasks.All(task => task.Source == AiProviderModes.DeepSeek && task.OpenUri == "dsh://open"),
                "the DeepSeek desktop store yields one task per conversation, labeled as DeepSeek");
            var waiting = tasks.Single(task => task.Id == "session-11111111-1111-1111-1111-111111111111");
            Check(waiting.Title == "优化仪表盘布局" && waiting.LatestUserMessage == "新增配额卡片" &&
                  waiting.Status == "waitingOnUserInput" && waiting.ModelLabel == "--",
                "the first sent prompt names the conversation, the last one is its latest message, and pending questions wait for input");
            var open = tasks.Single(task => task.Id == "session-22222222-2222-2222-2222-222222222222");
            Check(open.Title == "统一任务列表" && open.LatestUserMessage == "统一任务列表" &&
                  open.Status == "completed",
                "a conversation whose prompt is still being typed keeps that text as its name and latest message");

            // The same reader accepts UTF-8 values, which other Chromium builds write.
            var utf8Log = Path.Combine(levelDb, "000004.log");
            WriteLevelDbLog(utf8Log,
            [
                ("utf8+_dsh-app://app dsh.conversation.session-33333333-3333-3333-3333-333333333333",
                    Encoding.UTF8.GetBytes("""{"draft":{"text":"UTF-8 会话","references":[]}}"""))
            ]);
            var withUtf8 = reader.Read(60);
            Check(withUtf8.Any(task => task.Id == "session-33333333-3333-3333-3333-333333333333" &&
                                       task.Title == "UTF-8 会话"),
                "local-storage values are accepted in both UTF-16 and UTF-8 encodings");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifySnappyCodec()
    {
        var plain = Encoding.ASCII.GetBytes("abcabcabca");
        // Length 10, a three-byte literal, then a copy of 7 bytes from offset 3.
        var compressed = new List<byte> { 10, (3 - 1) << 2, (byte)'a', (byte)'b', (byte)'c', 26, 3, 0 };
        Check(SnappyCodec.Decompress(compressed.ToArray()).SequenceEqual(plain),
            "the Snappy decoder expands literal runs and overlapping back-references");

        var literal = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("LevelDB block payload ", 20)));
        Check(SnappyCodec.Decompress(SnappyLiteralCompress(literal)).SequenceEqual(literal),
            "the Snappy decoder restores a multi-chunk literal block");
    }

    private static void VerifyLevelDbTableReader()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-dsh-table-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "000005.ldb");
            var conversationKey = "+_dsh-app://app dsh.conversation.session-44444444-4444-4444-4444-444444444444";
            WriteLevelDbTable(path,
            [
                (conversationKey, DesktopValue("""{"draft":{"text":"压缩表里的会话","references":[]}}""")),
                ("META:dsh-app://app", Array.Empty<byte>())
            ], compress: true);

            var records = LevelDbTableReader.Read(path);
            Check(records.Any(record => record.Key == conversationKey &&
                                        DeepSeekDesktopTaskReader.DecodeValue(record.Value)
                                            ?.Contains("压缩表里的会话", StringComparison.Ordinal) == true),
                "compacted LevelDB tables are read through the block index, including Snappy-compressed blocks");

            var reader = new DeepSeekDesktopTaskReader(root);
            Check(reader.Read(60).Any(task => task.Id == "session-44444444-4444-4444-4444-444444444444"),
                "compact table records join the same conversation index as the write-ahead log");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Desktop conversations and Deep Code sessions must share the one DeepSeek task slot.</summary>
    private static void VerifyDeepSeekDesktopMergesIntoFeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-dsh-merge-" + Guid.NewGuid().ToString("N"));
        var levelDb = Path.Combine(root, "leveldb");
        var projects = Path.Combine(root, "projects", "demo");
        Directory.CreateDirectory(levelDb);
        Directory.CreateDirectory(projects);
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("O");
            WriteLevelDbLog(Path.Combine(levelDb, "000003.log"),
            [
                ("_dsh-app://app dsh.conversation.session-55555555-5555-5555-5555-555555555555",
                    DesktopValue("""{"draft":{"text":"桌面版会话","references":[]}}"""))
            ]);
            File.WriteAllText(Path.Combine(projects, "sessions-index.json"),
                "{\"originalPath\":\"F:\\\\Work\\\\Demo\",\"entries\":[" +
                $"{{\"id\":\"deep-thread\",\"summary\":\"Deep Code 会话\",\"status\":\"completed\",\"updateTime\":\"{stamp}\"}}" +
                "]}");
            var service = new AiManagerService(Path.Combine(root, "settings.json"),
                deepCodeTaskReader: new DeepCodeTaskReader(Path.Combine(root, "projects"),
                    Path.Combine(root, "settings.json"), null, TimeSpan.Zero),
                deepSeekDesktopTaskReader: new DeepSeekDesktopTaskReader(levelDb));
            try
            {
                AiTaskSnapshotFeed.Reset();
                var snapshot = service.RefreshDeepSeekTaskSnapshotAsync().GetAwaiter().GetResult();
                Check(snapshot.Tasks.Any(task => task.Id == "session-55555555-5555-5555-5555-555555555555") &&
                      snapshot.Tasks.Any(task => task.Id == "deep-thread") &&
                      snapshot.Tasks.All(task => task.IsDeepSeekSource),
                    "DeepSeek desktop conversations and Deep Code sessions share one DeepSeek task slot");
            }
            finally
            {
                AiTaskSnapshotFeed.Reset();
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string DesktopSessionPrefix(string id) => "_dsh-app://app ";

    /// <summary>Chromium writes LocalStorage values as UTF-16 behind one alignment byte.</summary>
    private static byte[] DesktopValue(string json)
    {
        var text = Encoding.Unicode.GetBytes(json);
        var buffer = new byte[text.Length + 1];
        Array.Copy(text, 0, buffer, 1, text.Length);
        return buffer;
    }

    private static void WriteLevelDbLog(string path, IEnumerable<(string Key, byte[] Value)> records)
    {
        var entries = records.ToList();
        using var stream = File.Create(path);
        var position = 0;
        var batch = new List<byte>();
        var sequence = 1L;
        foreach (var group in entries.Chunk(6))
        {
            var payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes(sequence++));
            payload.AddRange(BitConverter.GetBytes((uint)group.Length));
            foreach (var (key, value) in group)
            {
                payload.Add(1); // a put
                payload.AddRange(Varint(Encoding.UTF8.GetByteCount(key)));
                payload.AddRange(Encoding.UTF8.GetBytes(key));
                payload.AddRange(Varint(value.Length));
                payload.AddRange(value);
            }

            // One record per batch, kept small enough to never span a 32 KiB block.
            var record = new List<byte> { 0, 0, 0, 0 };
            record.AddRange(BitConverter.GetBytes((ushort)payload.Count));
            record.Add(1);
            record.AddRange(payload);
            stream.Write(record.ToArray());
            position += record.Count;
        }
    }

    private static void WriteLevelDbTable(string path, IEnumerable<(string Key, byte[] Value)> entries,
        bool compress)
    {
        var block = new List<byte>();
        var sorted = entries.OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
        foreach (var (key, value) in sorted)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            block.AddRange(Varint(0)); // shared prefix: none
            block.AddRange(Varint(keyBytes.Length));
            block.AddRange(Varint(value.Length));
            block.AddRange(keyBytes);
            block.AddRange(value);
        }
        block.AddRange(BitConverter.GetBytes(0)); // restart offset of the first entry
        block.AddRange(BitConverter.GetBytes(1)); // one restart point

        var contents = compress ? SnappyLiteralCompress(block.ToArray()) : block.ToArray();
        var dataBlock = new List<byte>(contents) { compress ? (byte)1 : (byte)0, 0, 0, 0, 0 };

        var indexBlock = new List<byte>();
        var lastKey = Encoding.UTF8.GetBytes(sorted.Last().Key);
        indexBlock.AddRange(Varint(0));
        indexBlock.AddRange(Varint(lastKey.Length));
        var handle = Varint(0).Concat(Varint(contents.Length)).ToArray();
        indexBlock.AddRange(Varint(handle.Length));
        indexBlock.AddRange(lastKey);
        indexBlock.AddRange(handle);
        indexBlock.AddRange(BitConverter.GetBytes(0));
        indexBlock.AddRange(BitConverter.GetBytes(1));
        var indexBlockTotal = new List<byte>(indexBlock) { 0, 0, 0, 0, 0 };

        var footer = new List<byte>();
        footer.AddRange(Varint(0)); // metaindex offset
        footer.AddRange(Varint(0)); // metaindex size
        footer.AddRange(Varint(dataBlock.Count)); // index offset: after the data block and its trailer
        footer.AddRange(Varint(indexBlock.Count)); // index size: content only, without the trailer
        while (footer.Count < 40) footer.Add(0);
        footer.AddRange(BitConverter.GetBytes(0xdb4775248b80fb57UL));

        using var stream = File.Create(path);
        stream.Write(dataBlock.ToArray());
        stream.Write(indexBlockTotal.ToArray());
        stream.Write(footer.ToArray());
    }

    /// <summary>Literal-only Snappy block: enough to exercise the decoder the table reader uses.</summary>
    private static byte[] SnappyLiteralCompress(byte[] input)
    {
        var output = new List<byte>();
        output.AddRange(Varint(input.Length));
        var index = 0;
        while (index < input.Length)
        {
            var chunk = Math.Min(input.Length - index, 60);
            output.Add((byte)((chunk - 1) << 2));
            for (var offset = 0; offset < chunk; offset++) output.Add(input[index + offset]);
            index += chunk;
            if (chunk < 60) break;
        }
        return output.ToArray();
    }

    private static IEnumerable<byte> Varint(int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            yield return (byte)(remaining | 0x80);
            remaining >>= 7;
        }
        yield return (byte)remaining;
    }
}
