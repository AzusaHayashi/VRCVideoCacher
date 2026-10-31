using System.Collections.Concurrent;
using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class AtomicFileTests : BridgeTestBase
{
    private string Path1 => Path.Combine(Root, "atomic.txt");

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        AtomicFile.WriteAllText(Path1, "hello");

        Assert.Equal("hello", AtomicFile.TryReadAllText(Path1));
    }

    [Fact]
    public void TryReadAllText_MissingFile_ReturnsNull()
    {
        Assert.Null(AtomicFile.TryReadAllText(Path.Combine(Root, "nope.txt")));
    }

    [Fact]
    public void Write_CreatesParentDirectory()
    {
        var deep = Path.Combine(Root, "a", "b", "c", "file.txt");

        AtomicFile.WriteAllText(deep, "x");

        Assert.True(File.Exists(deep));
        Assert.Equal("x", AtomicFile.TryReadAllText(deep));
    }

    [Fact]
    public void Write_LeavesNoTempFilesBehind()
    {
        AtomicFile.WriteAllText(Path1, "one");
        AtomicFile.WriteAllText(Path1, "two");

        var leftovers = Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories);

        Assert.Empty(leftovers);
    }

    [Fact]
    public void Write_CreatesBackupWithPreviousContent_WhenRequested()
    {
        var backup = Path.Combine(Root, "backup", "atomic.txt.bak");

        AtomicFile.WriteAllText(Path1, "version-one");
        AtomicFile.WriteAllText(Path1, "version-two", restrictAcl: false, backupPath: backup);

        Assert.True(File.Exists(backup));
        Assert.Equal("version-one", File.ReadAllText(backup));
        Assert.Equal("version-two", File.ReadAllText(Path1));
    }

    [Fact]
    public void Write_WithoutBackupPath_LeavesNoBackup()
    {
        AtomicFile.WriteAllText(Path1, "version-one");
        AtomicFile.WriteAllText(Path1, "version-two");

        Assert.Equal("version-two", File.ReadAllText(Path1));
        Assert.False(File.Exists(Path1 + ".cookiebridge.bak"));
    }

    /// <summary>
    /// The property that actually matters for VRCVideoCacher: a reader that opens the file at an arbitrary
    /// moment never observes a truncated jar. Every observed read must be one of the two complete versions.
    /// </summary>
    [Fact]
    public async Task ConcurrentReaders_NeverSeeAPartialFile()
    {
        const int size = 64 * 1024;
        var versionA = new string('A', size);
        var versionB = new string('B', size);
        var allowed = new HashSet<int> { versionA.Length, versionB.Length };

        AtomicFile.WriteAllText(Path1, versionA);

        var failures = new ConcurrentQueue<string>();
        var readsDone = 0;
        using var stop = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // A transient open failure during File.Replace is not a partial read; retry it briefly.
                string? text = null;
                for (var attempt = 0; attempt < 5 && text == null; attempt++)
                {
                    try
                    {
                        text = AtomicFile.TryReadAllText(Path1);
                    }
                    catch (IOException)
                    {
                        // file busy during the swap
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // ditto
                    }

                    if (text == null) Thread.Sleep(5);
                }

                Interlocked.Increment(ref readsDone);

                if (text == null)
                {
                    failures.Enqueue("read never succeeded");
                    return;
                }

                if (!allowed.Contains(text.Length))
                {
                    failures.Enqueue($"partial read: {text.Length} bytes, expected {size}");
                    return;
                }

                if (text.Length > 0 && text.Distinct().Count() != 1)
                {
                    failures.Enqueue("read mixed two versions");
                    return;
                }
            }
        })).ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(3);
        var iterations = 0;

        while (DateTime.UtcNow < deadline && failures.IsEmpty)
        {
            AtomicFile.WriteAllText(Path1, iterations++ % 2 == 0 ? versionB : versionA);
        }

        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(failures);
        Assert.True(iterations > 10, $"only {iterations} writes happened");
        Assert.True(readsDone > 10, $"only {readsDone} reads happened");
    }

    [Fact]
    public void RepeatedReplacement_AlwaysEndsWithTheLastCompleteVersion()
    {
        for (var i = 0; i < 50; i++)
            AtomicFile.WriteAllText(Path1, new string((char)('a' + i % 26), 4096));

        var final = File.ReadAllText(Path1);

        Assert.Equal(4096, final.Length);
        Assert.Single(final.Distinct());
    }
}
