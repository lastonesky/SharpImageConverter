using System;

public static class Adler32
{
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