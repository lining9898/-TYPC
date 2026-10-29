using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(CadDrawingNumber.Plugin))]
[assembly: CommandClass(typeof(CadDrawingNumber.Plugin))]
[assembly: CommandClass(typeof(CadDrawingNumber.DrawingNumberCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.DrawingNumberPaletteCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.BatchBlockCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.LinetypeCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.TextPrefixSuffixCommands))]

namespace CadDrawingNumber;

public sealed class Plugin : IExtensionApplication
{
    /// <summary>
    /// 当前版本号（主.次.修订），取自程序集版本，与 csproj 的 AssemblyVersion 同步。
    /// </summary>
    public static string VersionString
    {
        get
        {
            System.Version? version = typeof(Plugin).Assembly.GetName().Version;
            if (version is null)
                return "未知";
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public void Initialize()
    {
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            $"\nTYPC v{VersionString} 已加载。输入 TYPC 打开窗口；THBH 批量图号；" +
            "BBLK 分堆成块；WZXX/TAXX 快速线型；TYPCV 查看版本。");
    }

    public void Terminate()
    {
        DrawingNumberPaletteCommands.DisposePalette();
    }

    /// <summary>
    /// 在命令行输出当前插件版本号。
    /// </summary>
    [CommandMethod("TYPCV", CommandFlags.Modal)]
    public void ShowVersion()
    {
        Editor? editor = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (editor is null)
            return;

        editor.WriteMessage($"\nTYPC 版本：v{VersionString}");
    }
}
