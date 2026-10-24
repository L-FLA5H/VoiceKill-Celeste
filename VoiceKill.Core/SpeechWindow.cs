using System;

namespace VoiceKill.Core;

/// <summary>
/// 跨句保留证据。
/// 识别器遇到停顿就断句，「秒切」会被切成「秒」+「切」两截 ——
/// 这是 MC 版挨骂最多的问题，靠这张「上一句尾巴」的便签救回来。
/// </summary>
public sealed class SpeechWindow
{
    private const int TailChars = 8;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(5);

    private string _tail = "";
    private DateTime _tailAt = DateTime.MinValue;

    /// <summary>把上一句的尾巴接到这一句前面。</summary>
    public string Join(string text)
    {
        if (string.IsNullOrEmpty(_tail)) return text ?? "";
        if (DateTime.Now - _tailAt > FreshFor) return text ?? "";
        return _tail + (text ?? "");
    }

    /// <summary>记下这一句的最后几个字，供下一句拼接。</summary>
    public void Push(string text)
    {
        string norm = TextNormalizer.Normalize(text);
        if (norm.Length == 0) return;

        _tail = norm.Length <= TailChars ? norm : norm.Substring(norm.Length - TailChars);
        _tailAt = DateTime.Now;
    }

    public void Clear()
    {
        _tail = "";
        _tailAt = DateTime.MinValue;
    }
}
