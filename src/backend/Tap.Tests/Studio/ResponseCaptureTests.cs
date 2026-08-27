using System.Text;
using Tap.Execution.Http;
using Tap.Studio;

namespace Tap.Tests.Studio;

/// <summary>
/// Capturing an oversized response is the difference between "the rest is gone" and "the rest
/// is one click away", so the properties worth pinning are the boundaries: the inline copy is
/// exactly the cap, the retained copy starts at byte zero (or it can't be served as a file),
/// the true size is reported whatever we kept, and a response that fits never touches the disk.
/// </summary>
public class ResponseCaptureTests : IDisposable
{
    private readonly ResponseBodyStore _store = new();

    public void Dispose() => _store.Dispose();

    private static MemoryStream Body(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)('a' + i % 26);
        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task A_body_within_the_cap_is_captured_whole_and_never_spools()
    {
        await using var spool = _store.CreateSpool(1024);
        var captured = await ResponseCapture.ReadAsync(Body(500), 1024, spool, 4096, TestContext.Current.CancellationToken);

        Assert.Equal(500, captured.TotalBytes);
        Assert.Equal(500, captured.Inline.Length);
        Assert.False(captured.Truncated);
        Assert.Null(spool.SpilledPath);
        Assert.Null(_store.Publish(spool, "application/json", captured.TotalBytes));
    }

    [Fact]
    public async Task An_oversized_body_reports_its_true_size_and_keeps_only_the_cap_inline()
    {
        await using var spool = _store.CreateSpool(1024);
        var captured = await ResponseCapture.ReadAsync(Body(10_000), 1024, spool, 4096, TestContext.Current.CancellationToken);

        Assert.Equal(10_000, captured.TotalBytes);
        Assert.Equal(1024, captured.Inline.Length);
        Assert.True(captured.Truncated);
        Assert.True(captured.HasMoreRetained);
        Assert.Equal(4096, captured.RetainedBytes);
    }

    [Fact]
    public async Task The_retained_copy_is_a_prefix_from_byte_zero_not_the_overflow()
    {
        // The download hands this file to the user as the response; starting it at the cap
        // would produce a file that begins mid-JSON.
        var source = Body(10_000);
        var expected = source.ToArray();

        await using var spool = _store.CreateSpool(1024);
        var captured = await ResponseCapture.ReadAsync(source, 1024, spool, 4096, TestContext.Current.CancellationToken);
        await spool.FlushAsync(TestContext.Current.CancellationToken);
        var retained = _store.Publish(spool, "application/json", captured.TotalBytes);

        Assert.NotNull(retained);
        Assert.Equal(4096, retained.RetainedBytes);
        Assert.Equal(10_000, retained.TotalBytes);
        Assert.False(retained.IsComplete);

        await spool.DisposeAsync();
        var onDisk = await File.ReadAllBytesAsync(retained.Path, TestContext.Current.CancellationToken);
        Assert.Equal(expected[..4096], onDisk);
        Assert.Equal(expected[..1024], captured.Inline);
    }

    [Fact]
    public async Task A_retain_cap_above_the_body_keeps_all_of_it()
    {
        await using var spool = _store.CreateSpool(1024);
        var captured = await ResponseCapture.ReadAsync(Body(5_000), 1024, spool, 1024 * 1024, TestContext.Current.CancellationToken);
        await spool.FlushAsync(TestContext.Current.CancellationToken);
        var retained = _store.Publish(spool, "text/plain", captured.TotalBytes);

        Assert.NotNull(retained);
        Assert.True(retained.IsComplete);
        Assert.Equal(5_000, retained.RetainedBytes);
    }

    [Fact]
    public async Task A_binary_body_that_fits_inline_is_still_retained_when_asked_for()
    {
        // The panel never receives these bytes — a binary body reaches it as
        // "[binary N bytes — …]" — so the spool is the only thing a download can read, however
        // small the response was.
        await using var spool = _store.CreateSpool(1024);
        var captured = await ResponseCapture.ReadAsync(Body(500), 1024, spool, 4096, TestContext.Current.CancellationToken);
        Assert.Null(spool.SpilledPath);

        await spool.MaterializeAsync(TestContext.Current.CancellationToken);
        await spool.FlushAsync(TestContext.Current.CancellationToken);
        var retained = _store.Publish(spool, "application/octet-stream", captured.TotalBytes);

        Assert.NotNull(retained);
        Assert.True(retained.IsComplete);
        Assert.Equal(500, retained.RetainedBytes);

        await spool.DisposeAsync();
        var onDisk = await File.ReadAllBytesAsync(retained.Path, TestContext.Current.CancellationToken);
        Assert.Equal(500, onDisk.Length);
    }

    [Fact]
    public async Task Materializing_an_empty_body_leaves_nothing_to_publish()
    {
        // A 204 and a failed connection both land here; neither should leave a file behind or
        // offer a download of nothing.
        await using var spool = _store.CreateSpool(1024);
        await ResponseCapture.ReadAsync(new MemoryStream([]), 1024, spool, 4096, TestContext.Current.CancellationToken);

        await spool.MaterializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(spool.SpilledPath);
        Assert.Null(_store.Publish(spool, "application/octet-stream", 0));
    }

    [Fact]
    public void The_decoder_and_the_placeholder_test_agree()
    {
        // The two halves of "did the client actually get the body?" are coupled by a string
        // prefix; retaining binary bodies for download depends on them staying in step.
        Assert.True(HttpExecutionHelpers.IsBinaryPlaceholder(
            HttpExecutionHelpers.TryDecodeBody([0x00, 0x01, 0x02, 0xFF], "application/octet-stream", 4)));
        Assert.False(HttpExecutionHelpers.IsBinaryPlaceholder(
            HttpExecutionHelpers.TryDecodeBody("{}"u8.ToArray(), "application/json", 2)));
    }

    [Fact]
    public async Task Without_a_sink_nothing_beyond_the_cap_is_kept()
    {
        // The CLI / test-runner path: report the size, keep the prefix, retain nothing.
        var captured = await ResponseCapture.ReadAsync(
            Body(10_000), 1024, retain: null, maxRetained: 1024 * 1024, TestContext.Current.CancellationToken);

        Assert.Equal(10_000, captured.TotalBytes);
        Assert.Equal(1024, captured.RetainedBytes);
        Assert.False(captured.HasMoreRetained);
    }

    [Fact]
    public async Task A_published_body_is_retrievable_until_it_is_evicted()
    {
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            await using var spool = _store.CreateSpool(8);
            var captured = await ResponseCapture.ReadAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(new string((char)('a' + i), 64))),
                8, spool, 64, TestContext.Current.CancellationToken);
            await spool.FlushAsync(TestContext.Current.CancellationToken);
            ids.Add(_store.Publish(spool, "text/plain", captured.TotalBytes)!.Id);
        }

        // Bounded on purpose — the store is a convenience for the response you are looking at,
        // not a history.
        Assert.Null(_store.Get(ids[0]));
        Assert.Null(_store.Get(ids[1]));
        Assert.NotNull(_store.Get(ids[^1]));
    }

    [Fact]
    public async Task Disposing_the_store_removes_every_spool_file()
    {
        var store = new ResponseBodyStore();
        await using var spool = store.CreateSpool(8);
        var captured = await ResponseCapture.ReadAsync(Body(1024), 8, spool, 1024, TestContext.Current.CancellationToken);
        await spool.FlushAsync(TestContext.Current.CancellationToken);
        var retained = store.Publish(spool, "text/plain", captured.TotalBytes)!;
        await spool.DisposeAsync();

        Assert.True(File.Exists(retained.Path));
        store.Dispose();
        Assert.False(File.Exists(retained.Path));
    }
}
