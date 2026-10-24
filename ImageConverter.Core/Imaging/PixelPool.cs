using System.Collections.Concurrent;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Pool of reusable pixel buffers, keyed by exact length.
/// </summary>
/// <remarks>
/// <para>If the processing chain allocates new buffers for every step (460 MB each at 28.7 MP), the garbage collector
/// reclaims them late and the peak reaches about 8 GB. With the pool, the passes run with a few buffers that are reused
/// again and again.</para>
/// <para>Rules: only return a buffer that was rented by the same code and is not touched afterwards; never the input of
/// a caller or a buffer that is held in a cache. Rented buffers are not cleared. Call <see cref="Trim"/> after a large
/// computation, otherwise the memory stays allocated.</para>
/// <para><see cref="Rent"/> and <see cref="Return"/> may be called from several threads at once (concurrent
/// collections); the limit per length is approximate under concurrency.</para>
/// </remarks>
public static class PixelPool
{
    /// <summary>Maximum number of free buffers per length. The guided filter of the noise reduction needs 8 at the same
    /// time; with only 2, almost every buffer is allocated anew. <see cref="Trim"/> empties the pool after preview and
    /// export.</summary>
    private const int MaxFreePerLength = 8;

    private static readonly ConcurrentDictionary<int, ConcurrentBag<float[]>> Free = new();

    /// <summary>Rents a buffer of exactly <paramref name="length"/> elements.</summary>
    /// <param name="length">Number of <see cref="float"/> elements.</param>
    /// <returns>A free buffer from the pool or a newly allocated one; in both cases the content is undefined (not
    /// cleared).</returns>
    public static float[] Rent(int length)
    {
        if (Free.TryGetValue(length, out var bag) && bag.TryTake(out var buffer)) return buffer;
        return GC.AllocateUninitializedArray<float>(length);
    }

    /// <summary>Returns a buffer to the pool for reuse.</summary>
    /// <param name="buffer">Buffer that the caller owns and no longer uses. <see langword="null"/> and empty arrays are
    /// ignored; if the pool already holds the maximum number of free buffers of this length, the buffer is left to the
    /// garbage collector.</param>
    public static void Return(float[]? buffer)
    {
        if (buffer is null || buffer.Length == 0) return;
        var bag = Free.GetOrAdd(buffer.Length, _ => []);
        if (bag.Count < MaxFreePerLength) bag.Add(buffer);
    }

    /// <summary>Drops all free buffers (after an export or a preview).</summary>
    /// <param name="release">After a large computation (export): also return the memory to the operating system.
    /// Otherwise .NET keeps the large regions and the process stays at several GB.</param>
    /// <remarks>
    /// A normal collection (even with LOH compaction) does not return the memory: after exporting the panorama test
    /// image (28 MP), 4.1 GB stayed committed with 0.16 GB in use and 7.0 GB resident. Only
    /// <see cref="GCCollectionMode.Aggressive"/> releases it (afterwards 1.2 GB, a pause of about 0.1 s); on Linux,
    /// <c>malloc_trim</c> additionally releases the memory freed by ImageMagick. The image result is not affected.
    /// </remarks>
    public static void Trim(bool release = false)
    {
        Free.Clear();
        if (!release) return;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        // Whatever finalizers or post-collection callbacks release (for example shared buffer pools) is only reclaimed by
        // the next collection; measured, 2 GB stayed in use after a single collection
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                _ = malloc_trim(0);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // Other C library (musl): not needed
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int malloc_trim(nuint pad);
}
