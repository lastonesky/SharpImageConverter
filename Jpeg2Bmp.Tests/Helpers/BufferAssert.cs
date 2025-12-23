using Tests.Helpers;

namespace Tests.Helpers
{
    public static class BufferAssert
    {
        public static void EqualExact(byte[] a, byte[] b)
        {
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i], b[i]);
            }
        }

        public static double Mse(byte[] a, byte[] b)
        {
            Assert.AreEqual(a.Length, b.Length);
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += d * d;
            }
            return (double)sum / a.Length;
        }

        public static void AssertMseLessThan(byte[] a, byte[] b, double threshold)
        {
            double mse = Mse(a, b);
            Assert.IsTrue(mse <= threshold, $"MSE={mse} 超过阈值 {threshold}");
        }
    }
}
