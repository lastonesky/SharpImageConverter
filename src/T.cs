using System;

namespace SharpImageConverter;

/// <summary>
/// 简单的断言辅助类。
/// </summary>
public static class T
{
    /// <summary>
    /// 当条件为 false 时抛出异常。
    /// </summary>
    /// <param name="cond">断言条件</param>
    /// <param name="msg">失败信息</param>
    public static void Assert(bool cond, string msg)
    {
        if (!cond)
            throw new Exception("断言失败: " + msg);
    }
}
