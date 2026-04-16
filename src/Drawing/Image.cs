using System;
using System.IO;

namespace SharpImageConverter.Drawing;

/// <summary>
/// System.Drawing-like base image class.
/// </summary>
public abstract class Image : IDisposable
{
    private volatile bool disposed;
    private ImageFrame frame;
    private readonly object syncRoot = new();

    protected Image(ImageFrame frame)
    {
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    internal ImageFrame Frame
    {
        get
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();
                return frame;
            }
        }
    }

    protected object SyncRoot => syncRoot;
    internal object SyncObject => syncRoot;

    protected void ReplaceFrame(ImageFrame newFrame)
    {
        lock (syncRoot)
        {
            ThrowIfDisposed();
            frame = newFrame ?? throw new ArgumentNullException(nameof(newFrame));
        }
    }

    public int Width
    {
        get
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();
                return frame.Width;
            }
        }
    }

    public int Height
    {
        get
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();
                return frame.Height;
            }
        }
    }

    public Size Size => new(Width, Height);

    public static Image FromFile(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return new Bitmap(ImageFrame.Load(filename));
    }

    public static Image FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new Bitmap(ImageFrame.Load(stream));
    }

    public virtual void Save(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        lock (syncRoot)
        {
            ThrowIfDisposed();
            frame.Save(filename);
        }
    }

    public virtual void Save(Stream stream, ImageFormat format, int jpegQuality = 75)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (syncRoot)
        {
            ThrowIfDisposed();

            switch (format)
            {
                case ImageFormat.Bmp:
                    frame.SaveAsBmp(stream);
                    break;
                case ImageFormat.Png:
                    frame.SaveAsPng(stream);
                    break;
                case ImageFormat.Jpeg:
                    frame.SaveAsJpeg(stream, jpegQuality);
                    break;
                case ImageFormat.Gif:
                    frame.SaveAsGif(stream);
                    break;
                case ImageFormat.Webp:
                    throw new NotSupportedException("WebP stream save is not available in the current core API.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown image format.");
            }
        }
    }

    public virtual void Save(Stream stream, Imaging.ImageFormat format, int jpegQuality = 75)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (format.Equals(Imaging.ImageFormat.Bmp))
        {
            Save(stream, ImageFormat.Bmp, jpegQuality);
            return;
        }
        if (format.Equals(Imaging.ImageFormat.Png))
        {
            Save(stream, ImageFormat.Png, jpegQuality);
            return;
        }
        if (format.Equals(Imaging.ImageFormat.Jpeg))
        {
            Save(stream, ImageFormat.Jpeg, jpegQuality);
            return;
        }
        if (format.Equals(Imaging.ImageFormat.Gif))
        {
            Save(stream, ImageFormat.Gif, jpegQuality);
            return;
        }
        if (format.Equals(Imaging.ImageFormat.Webp))
        {
            Save(stream, ImageFormat.Webp, jpegQuality);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown image format.");
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
