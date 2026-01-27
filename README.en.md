# SharpImageConverter

English | [简体中文](README.md)

Pure C# image processing and format conversion library with minimal external dependencies (does not use System.Drawing). Supports JPEG/PNG/BMP/WebP/GIF conversions (including JPEG decoding and Baseline JPEG encoding). The primary usage is via the library API; the CLI has been split into a separate project.

## Motivation

This library was originally created to address several practical issues we encountered when using existing .NET imaging libraries in production:

- Cross-platform requirements: our services need to run reliably on Linux and other non-Windows environments, so we cannot depend on `System.Drawing`, which is effectively Windows-only. On modern .NET versions it also produces many “Windows only” warnings at build time, which is undesirable for long-term maintenance.
- Licensing and cost: we want to avoid components that introduce revenue-based commercial licensing. For example, ImageSharp requires a paid license for companies with annual revenue above 1M USD, which adds uncertainty to future commercial usage.
- Stability and operability: in our production services, using SkiaSharp led to native crashes in the unmanaged layer that brought down the entire managed process and restarted the service, while leaving very little actionable information for root cause analysis. We prefer a fully managed, more predictable stack where failures are easier to diagnose.
- For internal use only, with no concurrency pressure, and capable of handling typical product images.

## Features

### JPEG
- Decode Baseline and Progressive JPEG
- Huffman decode, dequantization, integer IDCT, YCbCr to RGB
- Auto-apply EXIF Orientation (rotate/flip)
- Encode Baseline JPEG from intermediate RGB with adjustable quality
- Supports common sampling factors (e.g. 4:4:4/4:2:2/4:2:0); chroma upsampling uses nearest-neighbor resampling

### PNG
- Read:
  - Core chunks: IHDR, PLTE, IDAT, IEND
  - Transparency: parse tRNS and alpha color types (Grayscale+Alpha / Truecolor+Alpha); supports RGB24 and RGBA32 output (use RGBA APIs to preserve alpha)
  - All filters: None, Sub, Up, Average, Paeth
  - Adam7 interlacing
  - Grayscale, Truecolor, Indexed; bit depth 1/2/4/8/16 (normalized to 8-bit on conversion)
- Write:
  - Save as Truecolor PNG (RGB24) and Truecolor+Alpha PNG (RGBA32)
  - Zlib (Deflate) compression using the Up filter (per-row differencing), with SIMD acceleration applied to the Up filtering step
  - No palette or additional metadata

### BMP
- Read: uncompressed 8/24/32-bit BMP (outputs unified as RGB24)
- Write: 24-bit RGB BMP
- Automatic row alignment padding

### GIF
- Read:
  - GIF87a/GIF89a
  - LZW decode, global/local palettes
  - Transparency: parse transparent index (Graphic Control Extension); frame composition with Restore to Background/Restore to Previous disposal methods
  - Interlacing; export frames to RGB
- Write:
  - Single-frame GIF89a; Octree color quantization (24-bit RGB -> 8-bit Index)
  - LZW compression; no transparency/animation metadata (delay, loop)

### WebP
- Read/Write WebP via native libwebp under `runtimes/`
- Unified decode to RGB24; select encoder based on output file extension
- Current WebP encode quality is fixed to 75 (may become configurable later)
- WebP implementation relies on Google's libwebp and related components (BSD-3-Clause License); see `THIRD-PARTY-NOTICES.md` for copyright and license details

### Intermediate Format
- `ImageFrame` as the intermediate structure for format conversion (currently `Rgb24`)
- Always load as RGB, then encode according to output extension

## What's New (v0.1.3)

- PNG: Significantly smaller output size for many images (for example, a PNG that was around 110 MB can now shrink to about 30 MB, depending on image content), with a slight improvement in encoding speed.
- JPEG: Noticeably faster decoding pipeline, plus a modest speedup on the encoding side.
- BMP: Faster writing; on the same machine, writing a ~400 MB BMP now takes about 270 ms instead of roughly 400 ms.

## Project Layout

```
SharpImageConverter/
├── src/                         # Library (public API)
│  ├── Core/                     # Core types like Image/Configuration
│  ├── Formats/                  # Format sniffing and adapters (JPEG/PNG/BMP/WebP/GIF)
│  ├── Processing/               # Mutate/Resize/Grayscale pipelines
│  ├── Metadata/                 # Metadata structures (Orientation, etc.)
│  ├── runtimes/                 # WebP native libraries (win-x64/linux-x64/osx-arm64)
│  └── SharpImageConverter.csproj
├── Cli/                         # Standalone CLI project
│  ├── Program.cs
│  └── SharpImageConverter.Cli.csproj
├── SharpImageConverter.Tests/   # Test project
└── README.md / README.en.md
```

## Usage (API)

Requirements:
- .NET SDK 8.0 or newer (library target frameworks: `net8.0;net10.0`)
- Windows/Linux/macOS (WebP requires loading the native libraries under `runtimes/` for your platform)

## Install (NuGet)

```bash
dotnet add package SharpImageConverter --version 0.1.4
```

Namespaces:

```csharp
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
```

Common examples:
- Load, process, and save (auto-sniff input format; choose encoder by output extension)

```csharp
// Load as RGB24
var image = Image.Load("input.jpg"); // API entry [Image](src/Core/Image.cs)

// Process: fit within 320x240, then grayscale
image.Mutate(ctx => ctx
    .ResizeToFit(320, 240)       // choose nearest-neighbor or bilinear via dedicated APIs
    .Grayscale());               // processing pipeline [Processing](src/Processing/Processing.cs)

// Save (encoder is selected by output extension)
Image.Save(image, "output.png");
```

- Clone and process (keep the original image unchanged)

```csharp
// Create a processed copy from an existing image
var processed = image.Clone(ctx => ctx
    .ResizeToFit(320, 240)
    .Grayscale());

// image remains unchanged, processed is the result
Image.Save(processed, "output_clone.png");
```

- RGBA mode (preserve alpha for load/save; unsupported targets automatically fall back to RGB saving)

```csharp
// Load RGBA32 (prefer native RGBA decoders when available)
var rgba = Image.LoadRgba32("input.png");
// Save to an alpha-supporting format (PNG/WebP/GIF); falls back to RGB if not supported
Image.Save(rgba, "output.webp");
```

- Stream operations:

```csharp
using SharpImageConverter; // ImageFrame

// Load from stream (auto-sniff format)
using var input = File.OpenRead("input.jpg");
var frame = ImageFrame.Load(input);

// Convert to Image<Rgb24> for processing
var image = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);
image.Mutate(ctx => ctx.Grayscale());

// Save to stream (specify format explicitly or use ImageFrame helpers)
using var output = new MemoryStream();
// Option A: Use ImageFrame helper
new ImageFrame(image.Width, image.Height, image.Buffer).SaveAsPng(output);
// Option B: Use specific encoder
// new SharpImageConverter.Formats.PngEncoderAdapter().EncodeRgb24(output, image);
```

## Command Line Interface (CLI)

Located in the `Cli/` directory, providing convenient format conversion and simple processing capabilities.

### Running

```bash
# Run from the Cli directory
dotnet run -- <input file or folder> [output file or folder] [operation] [options]
```

### Supported Formats
- Input: .jpg/.jpeg/.png/.bmp/.webp/.gif
- Output: .jpg/.jpeg/.png/.bmp/.webp/.gif

Special cases:
- GIF → WebP: when input is animated GIF and output extension is `.webp`, encodes animated WebP (preserving frame timing and loop count where possible)

### Operations
- `resize:WxH` : Force resize to specified width (W) and height (H)
- `resizebilinear:WxH` : Resize to specified size using bilinear interpolation
- `resizefit:WxH` : Resize to fit within specified rectangle (W x H) maintaining aspect ratio
- `grayscale` : Convert to grayscale

### Options
- `--quality N` : Set JPEG encoding quality (0-100), default 75
- `--subsample 420|444` : Set JPEG chroma subsampling, default 420
- `--keep-metadata` : When recompressing JPEG, try to preserve basic EXIF/ICC metadata
- `--jpeg-debug` : Enable JPEG decoding debug output
- `--gif-frames` : Export animated GIF frames as individual images for inspection/debugging
- `--stream` : Use streaming decode for JPEG to reduce memory usage
- `--idct int|float` : Choose JPEG IDCT implementation (integer/float), applies to JPEG decoding only

### Folder Batch Conversion
- Recursion: `--recursive` (traverse subdirectories)
- Output format: `--to bmp|png|jpg|webp` or `--out-ext .bmp|.png|.jpg|.webp`
- Parallelism: `--parallel N` (defaults to logical CPU count; for `.webp` output in directory mode, automatically reduces to 1 to ensure thread-safety)
- Skip existing: `--skip-existing` (skip if target file already exists)
- Output location: when the second argument is a folder, preserves the relative structure of the source directory; if not specified, outputs next to the source files
- Default extension: `.png` when no operation; `.bmp` when operations are present; explicit `--to/--out-ext` takes precedence
### Examples

```bash
# Convert format
dotnet run -- input.png output.jpg
dotnet run -- input.png output.jpg

# Resize and convert
dotnet run -- input.jpg output.png resize:800x600

# Resize to fit and set JPEG quality
dotnet run -- big.png thumb.jpg resizefit:200x200 --quality 90

# Batch: convert entire folder to PNG (default)
dotnet run -- d:\images

# Batch: specify output folder with recursion
dotnet run -- d:\images d:\out --recursive

# Batch: set output format to BMP and parallelism
dotnet run -- d:\images d:\out --to bmp --parallel 8

# Batch: skip existing files
dotnet run -- d:\images d:\out --skip-existing
```

## License

This project is licensed under the MIT License. Please note that the code in this repository is primarily generated with the assistance of AI.
