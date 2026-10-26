using System;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadDrawingNumber;

public sealed class LinetypeCommands
{
    private static string _textLinetypeName = "TYPC_TEXT";
    private static string _textContent = "文字";
    private static double _textHeight = 2.5;
    private static double _textDashLength = 10.0;
    private static double _textGap = 1.0;

    private static string _patternLinetypeName = "TYPC_PATTERN";
    private static PatternShape _patternShape = PatternShape.Circle;
    private static double _patternScale = 1.0;
    private static double _patternDashLength = 10.0;
    private static double _patternGap = 2.0;

    [CommandMethod("WZXX", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("TYY_TEXTLINE", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void CreateTextLinetype()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        if (!TryGetLinetypeName(editor, _textLinetypeName, out string name))
            return;
        _textLinetypeName = name;

        var textOptions = new PromptStringOptions(
            $"\n输入线型中显示的文字 <{_textContent}>: ")
        {
            AllowSpaces = true,
            DefaultValue = _textContent,
            UseDefaultValue = true
        };
        PromptResult textResult = editor.GetString(textOptions);
        if (textResult.Status != PromptStatus.OK)
            return;

        string content = textResult.StringResult.Trim();
        if (content.Length == 0 || content.Contains("\"") ||
            content.Contains("\r") || content.Contains("\n"))
        {
            editor.WriteMessage("\n线型文字不能为空，也不能包含双引号或换行。");
            return;
        }
        _textContent = content;

        if (!TryGetPositiveDouble(editor, "文字高度", ref _textHeight) ||
            !TryGetPositiveDouble(editor, "线段长度", ref _textDashLength) ||
            !TryGetPositiveDouble(editor, "文字两侧间距", ref _textGap))
        {
            return;
        }

        string textStyleName = GetCurrentTextStyleName(document.Database);
        string definition = string.Format(
            CultureInfo.InvariantCulture,
            "A,{0},-{1},[\"{2}\",{3},S={4},R=0.0,X={5},Y={6}],-{1}",
            _textDashLength,
            _textGap,
            _textContent,
            textStyleName,
            _textHeight,
            -_textGap / 2.0,
            -_textHeight / 2.0);

        CreateAndApplyLinetype(
            document,
            _textLinetypeName,
            "TYPC 文字线型 " + _textContent,
            definition);
    }

    [CommandMethod("TAXX", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("TYY_PATTERNLINE", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void CreatePatternLinetype()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        if (!TryGetLinetypeName(editor, _patternLinetypeName, out string name))
            return;
        _patternLinetypeName = name;

        var patternOptions = new PromptKeywordOptions(
            "\n选择图案 [圆点(C)/方框(B)/轨道(T)/保温棉(I)] <圆点>: ")
        {
            AllowNone = true,
            AppendKeywordsToMessage = false
        };
        patternOptions.Keywords.Add("C", "C", "圆点(C)");
        patternOptions.Keywords.Add("B", "B", "方框(B)");
        patternOptions.Keywords.Add("T", "T", "轨道(T)");
        patternOptions.Keywords.Add("I", "I", "保温棉(I)");
        patternOptions.Keywords.Default = GetPatternKeyword(_patternShape);
        PromptResult patternResult = editor.GetKeywords(patternOptions);
        if (patternResult.Status == PromptStatus.Cancel)
            return;
        if (patternResult.Status == PromptStatus.OK)
            _patternShape = ParsePatternShape(patternResult.StringResult);

        if (!TryGetPositiveDouble(editor, "图案缩放", ref _patternScale) ||
            !TryGetPositiveDouble(editor, "线段长度", ref _patternDashLength) ||
            !TryGetPositiveDouble(editor, "图案两侧间距", ref _patternGap))
        {
            return;
        }

        string shapeName = GetShapeName(_patternShape);
        string definition = string.Format(
            CultureInfo.InvariantCulture,
            "A,{0},-{1},[{2},ltypeshp.shx,S={3},R=0.0,X={4},Y=0.0],-{1}",
            _patternDashLength,
            _patternGap,
            shapeName,
            _patternScale,
            -_patternGap / 2.0);

        CreateAndApplyLinetype(
            document,
            _patternLinetypeName,
            "TYPC 图案线型 " + GetPatternDisplayName(_patternShape),
            definition);
    }

    private static bool TryGetLinetypeName(
        Editor editor,
        string current,
        out string name)
    {
        var options = new PromptStringOptions($"\n输入新线型名称 <{current}>: ")
        {
            AllowSpaces = false,
            DefaultValue = current,
            UseDefaultValue = true
        };
        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK)
        {
            name = string.Empty;
            return false;
        }

        name = result.StringResult.Trim();
        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, false);
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            editor.WriteMessage("\n线型名称无效，请勿使用空格或禁用字符。");
            return false;
        }
    }

    private static bool TryGetPositiveDouble(
        Editor editor,
        string label,
        ref double value)
    {
        var options = new PromptDoubleOptions(
            $"\n输入{label} <{FormatNumber(value)}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = value,
            UseDefaultValue = true
        };
        PromptDoubleResult result = editor.GetDouble(options);
        if (result.Status == PromptStatus.Cancel)
            return false;
        if (result.Status == PromptStatus.OK)
            value = result.Value;
        return true;
    }

    private static string GetCurrentTextStyleName(Database database)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        TextStyleTableRecord? style = transaction.GetObject(
            database.Textstyle, OpenMode.ForRead, false) as TextStyleTableRecord;
        string name = style?.Name ?? "STANDARD";
        if (name.IndexOfAny(new[] { ',', '[', ']' }) >= 0)
            name = "STANDARD";
        return name;
    }

    private static void CreateAndApplyLinetype(
        Document document,
        string name,
        string description,
        string patternDefinition)
    {
        Editor editor = document.Editor;
        Database database = document.Database;

        using (Transaction checkTransaction =
               database.TransactionManager.StartTransaction())
        {
            LinetypeTable table = (LinetypeTable)checkTransaction.GetObject(
                database.LinetypeTableId, OpenMode.ForRead, false);
            if (table.Has(name))
            {
                editor.WriteMessage($"\n线型“{name}”已存在，请换一个名称。");
                return;
            }
        }

        string temporaryFile = Path.Combine(
            Path.GetTempPath(), "tyy_" + Guid.NewGuid().ToString("N") + ".lin");
        string fileContent = "*" + name + "," + description + Environment.NewLine +
            patternDefinition + Environment.NewLine;

        try
        {
            File.WriteAllText(temporaryFile, fileContent, Encoding.Default);
            database.LoadLineTypeFile(name, temporaryFile);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        {
            editor.WriteMessage($"\n创建线型失败：{exception.Message}");
            return;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryFile))
                    File.Delete(temporaryFile);
            }
            catch (IOException)
            {
            }
        }

        using Transaction transaction = database.TransactionManager.StartTransaction();
        LinetypeTable linetypeTable = (LinetypeTable)transaction.GetObject(
            database.LinetypeTableId, OpenMode.ForRead, false);
        ObjectId linetypeId = linetypeTable[name];

        var applyOptions = new PromptKeywordOptions(
            "\n线型已创建，选择后续操作 [应用到对象(A)/设为当前(C)/仅创建(K)] <应用到对象>: ")
        {
            AllowNone = true,
            AppendKeywordsToMessage = false
        };
        applyOptions.Keywords.Add("A", "A", "应用到对象(A)");
        applyOptions.Keywords.Add("C", "C", "设为当前(C)");
        applyOptions.Keywords.Add("K", "K", "仅创建(K)");
        applyOptions.Keywords.Default = "A";
        PromptResult applyResult = editor.GetKeywords(applyOptions);
        if (applyResult.Status == PromptStatus.Cancel)
            return;

        string action = applyResult.Status == PromptStatus.None
            ? "A"
            : applyResult.StringResult;
        int updated = 0;
        if (string.Equals(action, "A", StringComparison.OrdinalIgnoreCase))
        {
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\n选择需要应用新线型的对象: "
                });
            if (selection.Status == PromptStatus.OK)
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    try
                    {
                        Entity? entity = transaction.GetObject(
                            id, OpenMode.ForWrite, false) as Entity;
                        if (entity is null)
                            continue;
                        entity.LinetypeId = linetypeId;
                        updated++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                    }
                }
            }
        }
        else if (string.Equals(action, "C", StringComparison.OrdinalIgnoreCase))
        {
            database.Celtype = linetypeId;
        }

        transaction.Commit();
        editor.Regen();
        editor.WriteMessage($"\n线型“{name}”已创建。" +
            (updated > 0 ? $" 已应用到 {updated} 个对象。" : string.Empty));
    }

    private static string GetPatternKeyword(PatternShape shape)
    {
        return shape switch
        {
            PatternShape.Box => "B",
            PatternShape.Track => "T",
            PatternShape.Insulation => "I",
            _ => "C"
        };
    }

    private static PatternShape ParsePatternShape(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "B" => PatternShape.Box,
            "T" => PatternShape.Track,
            "I" => PatternShape.Insulation,
            _ => PatternShape.Circle
        };
    }

    private static string GetShapeName(PatternShape shape)
    {
        return shape switch
        {
            PatternShape.Box => "BOX",
            PatternShape.Track => "TRACK1",
            PatternShape.Insulation => "BAT",
            _ => "CIRC1"
        };
    }

    private static string GetPatternDisplayName(PatternShape shape)
    {
        return shape switch
        {
            PatternShape.Box => "方框",
            PatternShape.Track => "轨道",
            PatternShape.Insulation => "保温棉",
            _ => "圆点"
        };
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private enum PatternShape
    {
        Circle,
        Box,
        Track,
        Insulation
    }
}
