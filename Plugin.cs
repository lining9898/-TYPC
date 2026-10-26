using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(CadDrawingNumber.Plugin))]
[assembly: CommandClass(typeof(CadDrawingNumber.DrawingNumberCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.DrawingNumberPaletteCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.BatchBlockCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.LinetypeCommands))]
[assembly: CommandClass(typeof(CadDrawingNumber.TextPrefixSuffixCommands))]

namespace CadDrawingNumber;

public sealed class Plugin : IExtensionApplication
{
    public void Initialize()
    {
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nTYPC 已加载。输入 TYPC 打开窗口；THBH 批量图号；BBLK 分堆成块；WZXX/TAXX 快速线型。");
    }

    public void Terminate()
    {
        DrawingNumberPaletteCommands.DisposePalette();
    }
}
