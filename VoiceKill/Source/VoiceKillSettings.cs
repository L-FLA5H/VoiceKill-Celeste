namespace Celeste.Mod.VoiceKill;

/// <summary>
/// 游戏内设置。
///
/// **一个属性都不放**：菜单完全由
/// <see cref="VoiceKillModule.CreateModMenuSection"/> 手搭（MiaoNet / CelesteTAS 都是这么做的）。
/// 只要这里留了属性，Everest 就会自动生成一行「开关」，
/// 用户会以为按钮没生效（2026-10-24 就踩了这个）。
///
/// 所有配置（含总开关）都在 voicekill.cfg。
/// </summary>
public class VoiceKillSettings : EverestModuleSettings
{
}
