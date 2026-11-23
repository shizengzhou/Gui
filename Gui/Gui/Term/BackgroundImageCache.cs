using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;

namespace Gui.Term;

/// <summary>
/// Process-wide cache of decoded "background-image" bitmaps. Every pane draws the same
/// CanvasBitmap instead of decoding and uploading its own copy, which is where the VRAM
/// duplication came from. A lease is reference-counted; the last release disposes the
/// bitmap. Entries are keyed by the file's path and write stamp, so editing the image
/// loads the new one without a restart.
/// </summary>
internal static class BackgroundImageCache
{
    private sealed class Entry
    {
        public string Key = string.Empty;
        public readonly List<BackgroundImageLease> Leases = new();
        public CanvasBitmap? Bitmap;
        public bool Loading = true;
    }

    // Every caller is on the UI thread (pane config and Win2D draw callbacks), so the
    // cache needs no lock; the load continuation resumes on that same context.
    // Windows paths are case-insensitive: different casings of the same file must share one
    // decoded bitmap, not create a second entry that never gets released together.
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file identity the cache keys on. Re-acquiring after the file changes picks up
    /// the new contents instead of handing out the old bitmap.
    /// </summary>
    public static string Stamp(string path)
    {
        if (path.Length == 0) return string.Empty;
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "missing";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Takes a reference to the decoded bitmap for <paramref name="path"/>.</summary>
    public static BackgroundImageLease Acquire(string path)
    {
        string key = path + "\n" + Stamp(path);
        bool created = false;
        if (!Entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry { Key = key };
            Entries.Add(key, entry);
            created = true;
        }

        // The lease exists before the load starts: a decode that completes synchronously
        // must still find a reference to hand its bitmap to.
        var lease = new BackgroundImageLease(key);
        entry.Leases.Add(lease);
        // An entry that already finished decoding hands its bitmap to the new reference
        // immediately; only a load still in flight has to keep a waiter.
        if (!entry.Loading) lease.Complete(entry.Bitmap);
        if (created)
        {
            Diag.Debug("bg.image", $"decode '{path}'");
            _ = LoadAsync(path, key);
        }
        else
        {
            Diag.Debug("bg.image", $"reuse '{path}' leases={entry.Leases.Count}");
        }
        return lease;
    }

    private static async Task LoadAsync(string path, string key)
    {
        CanvasBitmap? bitmap = null;
        try
        {
            bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path);
        }
        catch
        {
            // A missing or undecodable file leaves the plain background color in place.
        }

        if (!Entries.TryGetValue(key, out Entry? entry))
        {
            bitmap?.Dispose();
            return;
        }

        entry.Bitmap = bitmap;
        entry.Loading = false;
        foreach (BackgroundImageLease lease in entry.Leases.ToArray())
            lease.Complete(bitmap);

        // Every pane released while the decode was in flight.
        if (entry.Leases.Count == 0)
        {
            Entries.Remove(key);
            entry.Bitmap?.Dispose();
            entry.Bitmap = null;
        }
    }

    internal static void Release(BackgroundImageLease lease)
    {
        if (!Entries.TryGetValue(lease.Key, out Entry? entry)) return;
        entry.Leases.Remove(lease);
        if (entry.Loading || entry.Leases.Count > 0) return;

        Entries.Remove(lease.Key);
        entry.Bitmap?.Dispose();
        entry.Bitmap = null;
    }
}

/// <summary>
/// One pane's reference to a shared background bitmap. Releasing is idempotent, and the
/// callback is dropped entirely when the lease is released before the decode finishes.
/// </summary>
internal sealed class BackgroundImageLease : IDisposable
{
    private readonly List<Action<CanvasBitmap?>> _waiters = new();

    internal BackgroundImageLease(string key) => Key = key;

    internal string Key { get; }

    internal bool IsReleased { get; private set; }

    internal bool Ready { get; private set; }

    internal CanvasBitmap? Bitmap { get; private set; }

    /// <summary>Runs once the bitmap is decoded, immediately when it already is.</summary>
    public void OnReady(Action<CanvasBitmap?> ready)
    {
        if (IsReleased) return;
        if (Ready)
        {
            ready(Bitmap);
            return;
        }
        _waiters.Add(ready);
    }

    internal void Complete(CanvasBitmap? bitmap)
    {
        if (IsReleased) return;
        Ready = true;
        Bitmap = bitmap;
        // A callback is allowed to release its own lease, so drain the list first.
        Action<CanvasBitmap?>[] waiters = _waiters.ToArray();
        _waiters.Clear();
        foreach (Action<CanvasBitmap?> ready in waiters) ready(bitmap);
    }

    public void Dispose()
    {
        if (IsReleased) return;
        IsReleased = true;
        _waiters.Clear();
        BackgroundImageCache.Release(this);
    }
}
