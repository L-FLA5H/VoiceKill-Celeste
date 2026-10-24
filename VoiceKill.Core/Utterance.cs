using System;

namespace VoiceKill.Core;

/// <summary>一次「句末结算」的结果。</summary>
public sealed class Utterance
{
    /// <summary>递增编号，用来把「后台复核结果」和「当时那一句」对上。</summary>
    public long Id;

    /// <summary>流式引擎给出的原文。</summary>
    public string StreamingText = "";

    /// <summary>句末复核（SenseVoice）给出的原文，可能为空。</summary>
    public string ReviewText = "";

    /// <summary>加了标点的版本，只用于显示，绝不参与判定。</summary>
    public string DisplayText = "";

    /// <summary>和上一句拼起来后的文本（防止「秒」「切」被切成两截）。</summary>
    public string JoinedText = "";

    public DateTime At = DateTime.Now;

    public override string ToString() => StreamingText;
}
