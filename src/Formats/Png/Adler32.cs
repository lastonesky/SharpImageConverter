using System;

namespace SharpImageConverter;

/// <summary>
/// Adler-32 校验算法实现，用于 Zlib 校验。
/// </summary>
public static class Adler32
{
    /// <summary>
    /// 计算指定缓冲区片段的 Adler-32 校验值。
    /// </summary>
    /// <param name="buffer">输入数据</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数量</param>
    /// <returns>Adler-32 校验值</returns>
    public static uint Compute(byte[] buffer, int offset, int count)
    {
        uint s1 = 1;
        uint s2 = 0;
        
        // Adler-32 modulo value
        const uint MOD = 65521;

        for (int i = 0; i < count; i++)
        {
            s1 = (s1 + buffer[offset + i]) % MOD;
            s2 = (s2 + s1) % MOD;
        }

        return (s2 << 16) | s1;
    }
    
    // Allows updating an existing checksum
    /// <summary>
    /// 基于已有校验值继续更新 Adler-32 校验。
    /// </summary>
    /// <param name="adler">已有校验值</param>
    /// <param name="buffer">输入数据</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数量</param>
    /// <returns>更新后的校验值</returns>
    public static uint Update(uint adler, byte[] buffer, int offset, int count)
    {
        uint s1 = adler & 0xFFFF;
        uint s2 = (adler >> 16) & 0xFFFF;
        const uint MOD = 65521;

        for (int i = 0; i < count; i++)
        {
            s1 = (s1 + buffer[offset + i]) % MOD;
            s2 = (s2 + s1) % MOD;
        }

        return (s2 << 16) | s1;
    }
}
