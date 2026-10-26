using System;
using System.Drawing;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using CadDrawingNumber.UI;

namespace CadDrawingNumber;

public sealed class DrawingNumberPaletteCommands
{
    private static readonly Guid PaletteId =
        new("E93A82E0-11B7-43D7-A79A-C57A4A96C260");

    private static PaletteSet? _palette;

    [CommandMethod("TYPC", CommandFlags.Modal)]
    [CommandMethod("TYY", CommandFlags.Modal)]
    [CommandMethod("THUI", CommandFlags.Modal)]
    public void ShowPalette()
    {
        if (_palette is null)
        {
            _palette = new PaletteSet("CAD 工具箱", PaletteId)
            {
                Style = PaletteSetStyles.ShowAutoHideButton |
                    PaletteSetStyles.ShowCloseButton |
                    PaletteSetStyles.Snappable,
                DockEnabled = DockSides.Left | DockSides.Right,
                MinimumSize = new Size(260, 360),
                Size = new Size(300, 430),
                KeepFocus = false
            };
            _palette.Add("快捷工具", new ModernToolPanel());
        }

        _palette.Visible = true;
    }

    internal static void DisposePalette()
    {
        _palette?.Dispose();
        _palette = null;
    }
}
