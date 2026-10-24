using System;
using System.Text;

namespace VoiceKill.Core;

/// <summary>
/// 文本归一化。识别结果里大小写、空格、标点全都不一样，
/// 不统一就别指望能匹配上。
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char raw in s)
        {
            char c = raw;

            // 全角字母数字 → 半角
            if (c >= 'Ａ' && c <= 'Ｚ') c = (char)(c - 'Ａ' + 'a');
            else if (c >= 'ａ' && c <= 'ｚ') c = (char)(c - 'ａ' + 'a');
            else if (c >= '０' && c <= '９') c = (char)(c - '０' + '0');
            else if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');

            if (c >= 'a' && c <= 'z') { sb.Append(c); continue; }
            if (c >= '0' && c <= '9') { sb.Append(c); continue; }

            // 中日韩统一表意文字
            if (c >= 0x4E00 && c <= 0x9FFF) { sb.Append(c); continue; }

            // 空格和标点一律丢掉 —— 这样「q a q」会被拼成「qaq」
        }
        return sb.ToString();
    }

    public static bool HasAsciiLetter(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
            if (c >= 'a' && c <= 'z') return true;
        return false;
    }

    public static int VisibleLength(string s) => string.IsNullOrEmpty(s) ? 0 : s.Length;
}
