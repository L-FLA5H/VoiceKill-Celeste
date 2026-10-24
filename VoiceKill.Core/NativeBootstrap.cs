using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VoiceKill.Core;

/// <summary>
/// 原生库加载器。
///
/// 为什么需要这一层：
/// sherpa-onnx 的 DllImport 名字是 "sherpa-onnx-c-api"，它依赖同目录的 onnxruntime.dll。
/// 游戏进程的工作目录是 Celeste 根目录，不是 mod 的 bin 目录 ——
/// 默认搜索路径找不到 onnxruntime.dll，就会报
/// 「Unable to load DLL 'sherpa-onnx-c-api' or one of its dependencies (0x8007007E)」。
///
/// 解法：用 LoadLibraryExW + LOAD_WITH_ALTERED_SEARCH_PATH 按**绝对路径**加载，
/// 这个标志会让依赖项从 DLL 自己所在的目录开始找。
/// </summary>
public static class NativeBootstrap
{
    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    private static readonly object Gate = new object();
    private static bool _ok;
    private static string _error = "";

    public static IntPtr ApiHandle { get; private set; }
    public static bool IsLoaded => _ok;

    /// <param name="dir">放着 onnxruntime.dll / sherpa-onnx-c-api.dll 的目录（mod 的 bin）</param>
    public static bool EnsureLoaded(string dir, out string error)
    {
        lock (Gate)
        {
            if (_ok) { error = null; return true; }

            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    throw new DirectoryNotFoundException("原生库目录不存在: '" + dir + "'");

                string ort = Path.Combine(dir, "onnxruntime.dll");
                string api = Path.Combine(dir, "sherpa-onnx-c-api.dll");
                if (!File.Exists(api)) api = Path.Combine(dir, "libsherpa-onnx-c-api.dll");
                if (!File.Exists(api))
                    throw new FileNotFoundException("目录里没有 sherpa-onnx-c-api.dll");

                // 先 onnxruntime，再 c-api —— 依赖顺序不能反
                if (File.Exists(ort))
                {
                    IntPtr hOrt = LoadLibraryExW(ort, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (hOrt == IntPtr.Zero)
                        throw new Exception("onnxruntime.dll 加载失败 (Win32 " + Marshal.GetLastWin32Error() + ")");
                }

                IntPtr hApi = LoadLibraryExW(api, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (hApi == IntPtr.Zero)
                    throw new Exception("sherpa-onnx-c-api.dll 加载失败 (Win32 " + Marshal.GetLastWin32Error() + ")");

                ApiHandle = hApi;
                _ok = true;
                _error = "";
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _ok = false;
                _error = ex.Message;
                error = ex.Message;
                return false;
            }
        }
    }

    public static string LastError => _error;
}
