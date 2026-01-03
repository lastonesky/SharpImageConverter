# SharpImageConverter

 Pure C# image processing and format conversion library with minimal external dependencies (does not use System.Drawing). Supports JPEG/PNG/BMP/WebP/GIF conversions (including JPEG decoding and Baseline JPEG encoding). The primary usage is via the library API; the CLI has been split into a separate project.

## Features

### JPEG
- Decode Baseline and Progressive JPEG
- Huffman decode, dequantization, IDCT, YCbCr to RGB
- Auto-apply EXIF Orientation (rotate/flip)
- Encode Baseline JPEG from intermediate RGB with adjustable quality
- Chroma upsampling optimizations: fast paths for 4:2:0 and 4:2:2; general 16.16 fixed-point bilinear fallback

### PNG
- Read:
  - Core chunks: IHDR, PLTE, IDAT, IEND
  - Transparency: parse tRNS and alpha color types (Grayscale+Alpha / Truecolor+Alpha); output unified as RGB24, alpha not preserved
  - All filters: None, Sub, Up, Average, Paeth
  - Adam7 interlacing
  - Grayscale, Truecolor, Indexed; bit depth 1/2/4/8/16 (normalized to 8-bit on conversion)
- Write:
  - Truecolor PNG (RGB24)
  - Zlib (Deflate) compression, filter fixed to None
  - No palette or additional metadata

### BMP
- Read/Write 24-bit RGB BMP
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
- Read/Write WebP via native libwebp under `src/runtimes/`
- Unified decode to RGB24; select encoder based on output file extension
- Current WebP encode quality is fixed to 75 (may become configurable later)

### Intermediate Format
- `ImageFrame` as the intermediate structure for format conversion (currently `Rgb24`)
- Always load as RGB, then encode according to output extension

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

Namespaces:

```csharp
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
```

Common examples:
- Load, process, and save (auto-sniff input format; choose encoder by output extension)

```csharp
// Load as RGB24
var image = Image.Load("input.jpg"); // API entry [Image](file:///d:/Project/jpeg2bmp/src/Core/Image.cs#L51-L95)

// Process: fit within 320x240, then grayscale
image.Mutate(ctx => ctx
    .ResizeToFit(320, 240)       // choose nearest-neighbor or bilinear via dedicated APIs
    .Grayscale());               // processing pipeline [Processing](file:///d:/Project/jpeg2bmp/src/Processing/Processing.cs#L18-L143)

// Save (encoder is selected by output extension)
Image.Save(image, "output.png");
```

- RGBA mode (preserve alpha for load/save; unsupported targets automatically fall back to RGB saving)

```csharp
// Load RGBA32 (prefer native RGBA decoders when available)
var rgba = Image.LoadRgba32("input.png");
// Save to an alpha-supporting format (PNG/WebP/GIF); falls back to RGB if not supported
Image.Save(rgba, "output.webp");
```

- Load from Stream (auto-sniff header)

```csharp
using var fs = File.OpenRead("input.jpg");
var frame = SharpImageConverter.ImageFrame.Load(fs); // entry [ImageFrame.Load(Stream)](file:///d:/Project/jpeg2bmp/src/ImageFrame.cs#L69-L114)
var imageFromStream = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);
```

- clone and resize usage

```csharp
var original = Image.Load("input.jpg");
var clone = new Image<Rgb24>(original.Width, original.Height, (byte[])original.Buffer.Clone());
clone.Mutate(ctx => ctx.Resize(640, 480));
Image.Save(clone, "resized.png");
```

Notes:
- API entry is [Image](file:///d:/Project/jpeg2bmp/src/Core/Image.cs#L51-L95), which uses [Configuration](file:///d:/Project/jpeg2bmp/src/Core/Configuration.cs#L20-L55) for format sniffing and encoder routing.
- Saving for RGB24 and RGBA32 selects the encoder by extension, see [Configuration.SaveRgb24](file:///d:/Project/jpeg2bmp/src/Core/Configuration.cs#L77-L93) and [Configuration.SaveRgba32](file:///d:/Project/jpeg2bmp/src/Core/Configuration.cs#L127-L146).
- The processing pipeline uses the `Mutate` extension to build a context; see [ImageExtensions.Mutate](file:///d:/Project/jpeg2bmp/src/Processing/Processing.cs#L148-L160).

<details>
<summary>CLI Usage (collapsed; click to expand)</summary>

## CLI

Requirements:
- .NET SDK (net10.0 preview)
- Windows/Linux/macOS (WebP native libraries are currently provided for win-x64/linux-x64/osx-arm64)

CLI:

```bash
dotnet run -- <input-path> [output-path] [operation] [--quality N]
```

Supported conversions:
- JPEG/PNG/BMP/WebP/GIF -> JPEG/PNG/BMP/WebP/GIF

The program auto-detects the input format via file extension (.jpg/.jpeg/.png/.bmp/.webp/.gif) and chooses the encoder by the output extension (.jpg/.jpeg/.png/.bmp/.webp/.gif).

Operations (optional):
- resize:WxH
- resizefit:WxH
- grayscale

JPEG options (optional):
- `--quality N`
- `--subsample 420/444`
- `--jpeg-debug`

Examples:

```bash
# JPEG -> PNG
dotnet run -- image.jpg image.png

# PNG -> BMP
dotnet run -- icon.png icon.bmp

# BMP -> PNG
dotnet run -- screenshot.bmp screenshot.png

# PNG -> JPEG
dotnet run -- icon.png icon.jpg

# JPEG -> JPEG (re-encode)
dotnet run -- image.jpg image_reencode.jpg

# JPEG -> WebP
dotnet run -- image.jpg image.webp

# WebP -> PNG
dotnet run -- image.webp image.png

# GIF -> PNG (export first frame)
dotnet run -- animation.gif frame_0.png

# PNG -> GIF (single-frame)
dotnet run -- image.png image.gif

# Export all GIF frames (output extension decides each frame format)
dotnet run -- animation.gif frames_000.png --gif-frames

# Convert and fit into 320x240
dotnet run -- image.jpg out.webp resizefit:320x240
```
</details>

## Known Limitations

- Non-standard subsampling ratios (other than 4:2:0 / 4:2:2 / 4:4:4) fall back to general fixed-point bilinear path, which is relatively slower.
- PNG writing supports Truecolor (RGB24) only; does not preserve palette, metadata, or alpha channel from source.
- JPEG encoding currently supports Baseline only (not Progressive); defaults to 4:2:0 subsampling; does not write EXIF or other metadata.
- WebP encoding options are limited for now (fixed quality); more options may be added later.
- GIF writing supports single frame only; does not write transparency or animation metadata (delay, loop).

## Roadmap

- Further optimize color conversion and upsampling performance for JPEG (SIMD/Vectorization, block-wise 2×2 processing).
- Improve robustness for special/edge-case JPEG variants.
- Expand unit tests and sample images.
- Enhance WebP encoding options (quality/lossless/alpha) and cross-platform runtime layout.

## Troubleshooting

- “File not found”: Check `Program.InputPath` is correct.
- Parse/decode exceptions: Verify the input file is not corrupted.
- WebP read/write DllNotFoundException (0x8007007E):
  - Ensure output directory contains `runtimes/win-x64/*.dll` or `runtimes/linux-x64/*.so*` (on macOS, `runtimes/osx-arm64/native/*.dylib`)
  - On Windows, include: `libwebp.dll`, `libwebpdecoder.dll` (some versions also require `libsharpyuv.dll`)
  - Some `libwebp.dll` builds depend on `libsharpyuv.dll` and the Microsoft Visual C++ 2015–2022 x64 runtime. Place required DLLs alongside the executable or install the VC++ runtime.

## License

See `LICENSE` for details.

