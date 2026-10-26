using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadDrawingNumber;

public sealed class TextPrefixSuffixCommands
{
    [CommandMethod("WZQF", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("TEXTAFFIX", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void AddPrefixSuffix()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        Database database = document.Database;

        // 1. 选择加注方式
        var modeOptions = new PromptKeywordOptions(
            "\n选择加注方式 [前缀(P)/后缀(S)/都加(B)] <都加>: ")
        {
            AllowNone = true,
            AppendKeywordsToMessage = false
        };
        modeOptions.Keywords.Add("P", "P", "前缀(P)");
        modeOptions.Keywords.Add("S", "S", "后缀(S)");
        modeOptions.Keywords.Add("B", "B", "都加(B)");
        modeOptions.Keywords.Default = "B";
        PromptResult modeResult = editor.GetKeywords(modeOptions);
        if (modeResult.Status == PromptStatus.Cancel)
            return;

        bool addPrefix = !modeResult.Status.Equals(PromptStatus.None)
            ? !string.Equals(modeResult.StringResult, "S",
                StringComparison.OrdinalIgnoreCase)
            : true;
        bool addSuffix = !modeResult.Status.Equals(PromptStatus.None)
            ? !string.Equals(modeResult.StringResult, "P",
                StringComparison.OrdinalIgnoreCase)
            : true;

        string prefix = string.Empty;
        string suffix = string.Empty;

        if (addPrefix)
        {
            var prefixOptions = new PromptStringOptions(
                "\n输入前缀: ")
            {
                AllowSpaces = true
            };
            PromptResult prefixResult = editor.GetString(prefixOptions);
            if (prefixResult.Status != PromptStatus.OK)
                return;
            prefix = prefixResult.StringResult ?? string.Empty;
        }

        if (addSuffix)
        {
            var suffixOptions = new PromptStringOptions(
                "\n输入后缀: ")
            {
                AllowSpaces = true
            };
            PromptResult suffixResult = editor.GetString(suffixOptions);
            if (suffixResult.Status != PromptStatus.OK)
                return;
            suffix = suffixResult.StringResult ?? string.Empty;
        }

        if (prefix.Length == 0 && suffix.Length == 0)
        {
            editor.WriteMessage("\n前缀和后缀都为空，操作取消。");
            return;
        }

        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\n选择要处理的文字（可框选或点选）: "
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "TEXT,MTEXT,ATTRIB")
        });
        PromptSelectionResult selection =
            editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK || selection.Value.Count == 0)
        {
            editor.WriteMessage("\n未选择任何文字。");
            return;
        }

        int changed = 0;
        int skipped = 0;

        using (Transaction transaction =
            database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in selection.Value.GetObjectIds())
            {
                try
                {
                    DBObject entity = transaction.GetObject(
                        id, OpenMode.ForRead, false);
                    if (ApplyPrefixSuffix(entity, prefix, suffix))
                        changed++;
                    else
                        skipped++;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    skipped++;
                }
            }

            transaction.Commit();
        }

        editor.WriteMessage(
            $"\n完成：{changed} 个文字已修改，{skipped} 个已跳过。");
    }

    private static bool ApplyPrefixSuffix(
        DBObject entity,
        string prefix,
        string suffix)
    {
        entity.UpgradeOpen();

        switch (entity)
        {
            case AttributeReference attribute:
            {
                string current = NumberingAttributeSupport.ReadText(attribute);
                if (string.IsNullOrWhiteSpace(current))
                    return false;
                NumberingAttributeSupport.WriteText(
                    attribute, prefix + current + suffix);
                return true;
            }
            case DBText text:
            {
                if (string.IsNullOrWhiteSpace(text.TextString))
                    return false;
                text.TextString = prefix + text.TextString + suffix;
                text.AdjustAlignment(text.Database);
                return true;
            }
            case MText mtext:
            {
                if (string.IsNullOrWhiteSpace(mtext.Contents))
                    return false;
                mtext.Contents = prefix + mtext.Contents + suffix;
                return true;
            }
            default:
                return false;
        }
    }
}
