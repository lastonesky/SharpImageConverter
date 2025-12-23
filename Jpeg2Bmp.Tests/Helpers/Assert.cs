using System;

namespace Tests.Helpers
{
    public static class Assert
    {
        public static void AreEqual<T>(T expected, T actual)
        {
            if (!Equals(expected, actual)) throw new Exception($"Assert.AreEqual 失败: 期望 {expected}, 实际 {actual}");
        }

        public static void IsTrue(bool condition, string? message = null)
        {
            if (!condition) throw new Exception(message ?? "Assert.IsTrue 失败");
        }

        public static void IsFalse(bool condition, string? message = null)
        {
            if (condition) throw new Exception(message ?? "Assert.IsFalse 失败");
        }
    }
}
