using System;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Drawing;

/// <summary>
/// Minimal Graphics-compatible drawing surface.
/// </summary>
public sealed class Graphics : IDisposable
{
    private readonly Bitmap target;
    private bool disposed;

    private Graphics(Bitmap target)
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public static Graphics FromImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not Bitmap bitmap)
        {
            throw new NotSupportedException("Only Bitmap-backed images are supported.");
        }

        return new Graphics(bitmap);
    }

    public void DrawImage(Image image, int x, int y)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(image);

        if (image is not Bitmap sourceBitmap)
        {
            throw new NotSupportedException("Only Bitmap source is supported.");
        }

        if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        LockBoth(target, sourceBitmap, static (dst, src, state) =>
        {
            int offsetX = state.x;
            int offsetY = state.y;

            ImageFrame dstFrame = dst.Frame;
            ImageFrame srcFrame = src.Frame;

            int copyLeft = Math.Max(0, offsetX);
            int copyTop = Math.Max(0, offsetY);
            int srcLeft = Math.Max(0, -offsetX);
            int srcTop = Math.Max(0, -offsetY);

            int copyWidth = Math.Min(srcFrame.Width - srcLeft, dstFrame.Width - copyLeft);
            int copyHeight = Math.Min(srcFrame.Height - srcTop, dstFrame.Height - copyTop);
            if (copyWidth <= 0 || copyHeight <= 0)
            {
                return;
            }

            int srcStride = srcFrame.Width * 3;
            int dstStride = dstFrame.Width * 3;
            int rowCopyBytes = copyWidth * 3;

            for (int row = 0; row < copyHeight; row++)
            {
                int srcOffset = ((srcTop + row) * srcStride) + (srcLeft * 3);
                int dstOffset = ((copyTop + row) * dstStride) + (copyLeft * 3);
                Buffer.BlockCopy(srcFrame.Pixels, srcOffset, dstFrame.Pixels, dstOffset, rowCopyBytes);
            }
        }, (x, y));
    }

    public void Dispose()
    {
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void LockBoth<TState>(Bitmap first, Bitmap second, Action<Bitmap, Bitmap, TState> action, TState state)
    {
        object firstLock = first.SyncObject;
        object secondLock = second.SyncObject;

        if (ReferenceEquals(firstLock, secondLock))
        {
            lock (firstLock)
            {
                action(first, second, state);
            }
            return;
        }

        int firstHash = RuntimeHelpers.GetHashCode(firstLock);
        int secondHash = RuntimeHelpers.GetHashCode(secondLock);

        if (firstHash < secondHash)
        {
            lock (firstLock)
            {
                lock (secondLock)
                {
                    action(first, second, state);
                }
            }
            return;
        }

        lock (secondLock)
        {
            lock (firstLock)
            {
                action(first, second, state);
            }
        }
    }
}
