using System;

namespace PictureSharp;

public static class T
{
    public static void Assert(bool cond, string msg)
    {
        if (!cond)
            throw new Exception("断言失败: " + msg);
    }
}
