using System;
using System.Diagnostics;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Formats.Jpeg;

class Program {
    static void Main(string[] args) {
        if (args.Length == 0) return;
        byte[] data = File.ReadAllBytes(args[0]);
        
        // Warmup
        for (int i = 0; i < 5; i++) {
            var img = ImageFrame.Load(data);
        }

        Stopwatch sw = Stopwatch.StartNew();
        int count = 50;
        for (int i = 0; i < count; i++) {
            var img = ImageFrame.Load(data);
        }
        sw.Stop();
        Console.WriteLine($"Average time: {sw.ElapsedMilliseconds / (double)count} ms");
    }
}
