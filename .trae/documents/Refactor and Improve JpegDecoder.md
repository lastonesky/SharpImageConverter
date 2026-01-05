I have analyzed `d:\Project\jpeg2bmp\src\Formats\Jpeg\JpegDecoder.cs` and identified several issues related to code structure, performance, and error handling.

### Analysis
1.  **Architecture**: The file is monolithic (~1500 lines) and contains multiple internal classes (`JpegBitReader`, `JpegIDCT`, `FrameHeader`, etc.) that should be separated.
2.  **Duplication**: It defines internal `QuantizationTable` and `HuffmanTable` classes while `JpegQuantTable.cs` and `JpegHuffmanTable.cs` already exist in the project.
3.  **Performance**:
    -   `DecodeToRGB` reads the entire stream into a `byte[]` array (`ms.ToArray()`), which is inefficient for large images.
    -   `PerformIDCTAndOutput` allocates many small arrays inside loops.
4.  **Error Handling**: Uses `Console.WriteLine` for warnings/errors and generic `Exception` throws.
5.  **Features**: Returns raw `byte[]` instead of a structured `Image<Rgb24>`.

### Plan
I will refactor the code in the following steps:

#### Phase 1: Code Structure & Cleanup
1.  **Extract Internal Classes**: Move `JpegMarkers`, `JpegIDCT`, `JpegBitReader`, `FrameHeader`, `ScanHeader`, `Component` to separate files.
2.  **Unify Data Structures**: Update `JpegDecoder` to use the existing `JpegQuantTable` and `JpegHuffmanTable` classes. Create a `HuffmanDecodingTable` wrapper for the decoding-specific lookup tables (`MaxCode`, `ValPtr`, etc.).
3.  **Remove Logging**: Replace `Console.WriteLine` with proper exception handling or a logging abstraction (if available) or simply suppress debug output.
4.  **Typed Exceptions**: Replace generic `throw new Exception(...)` with `InvalidDataException` or `NotSupportedException`.

#### Phase 2: Performance & Modernization
5.  **Stream-based Reading**: Refactor `JpegBitReader` and `JpegDecoder` to read directly from the `Stream` instead of loading the entire file into `byte[]`.
6.  **Return Type Update**: Change `DecodeToRGB` to return `Image<Rgb24>` (using `SharpImageConverter.Core.Image<T>`).
7.  **Allocation Optimization**: Optimize `PerformIDCTAndOutput` to reuse buffers.

#### Phase 3: Verification
8.  **Test**: Verify the changes by running existing tests (if any) or creating a simple test case to decode a JPEG image.

I will start with Phase 1 (Extraction and Cleanup).
