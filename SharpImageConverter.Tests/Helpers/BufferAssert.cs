using System;
using Xunit;
using Xunit.Sdk;

namespace SharpImageConverter.Tests.Helpers
{
    public static class BufferAssert
    {
        /// <summary>
        /// 逐字节断言两段缓冲完全一致。
        /// </summary>
        /// <remarks>
        /// 不用 <c>for + Assert.Equal(byte,byte)</c>：那样每个字节都要付一次断言框架开销，
        /// 3 MP 的图就要 300 万次调用（实测约 1.4 s）。这里先用 <see cref="MemoryExtensions.SequenceEqual{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>
        /// 一次性比较，只有失败时才定位首个差异字节，失败信息比 xUnit 默认的数组 diff 更精确。
        /// </remarks>
        public static void EqualExact(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
        {
            if (expected.SequenceEqual(actual))
            {
                return;
            }

            if (expected.Length != actual.Length)
            {
                throw new XunitException($"缓冲长度不一致: expected={expected.Length} actual={actual.Length}");
            }

            int index = 0;
            while (expected[index] == actual[index])
            {
                index++;
            }

            throw new XunitException(
                $"第 {index} 字节不一致: expected=0x{expected[index]:X2} actual=0x{actual[index]:X2}" +
                $"（共 {expected.Length} 字节，前 {index} 字节相同）");
        }

        public static double Mse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Assert.Equal(a.Length, b.Length);
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += d * d;
            }
            return (double)sum / a.Length;
        }

        public static void AssertMseLessThan(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, double threshold)
        {
            double mse = Mse(a, b);
            Assert.True(mse <= threshold, $"MSE={mse} 超过阈值 {threshold}");
        }
    }
}
