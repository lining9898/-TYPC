using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadDrawingNumber;

public sealed class DrawingNumberCommands
{
    private const string CanonicalPlaceholder = "{{图号}}";
    private const string MarkerKey = "IFX_DRAWING_NUMBER";

    private static readonly Regex DrawingNumberTokenPattern = new(
        @"[A-Za-z\u4e00-\u9fff]{1,12}\s*[-_.]\s*\d{1,6}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string _placeholder = CanonicalPlaceholder;
    private static string? _drawingNumberLayer;

    [CommandMethod("THBH", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("IFX_DRAWINGNO", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void SetDrawingNumber()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        var sourceOptions = new PromptKeywordOptions(
            "\n选择批量编号方式 [图层(L)/占位符(P)/图框属性(A)] <图层>: ")
        {
            AllowNone = true,
            AppendKeywordsToMessage = false
        };
        sourceOptions.Keywords.Add("L", "L", "图层(L)");
        sourceOptions.Keywords.Add("P", "P", "占位符(P)");
        sourceOptions.Keywords.Add("A", "A", "图框属性(A)");
        sourceOptions.Keywords.Default = "L";
        PromptResult sourceResult = editor.GetKeywords(sourceOptions);
        if (sourceResult.Status == PromptStatus.Cancel)
            return;

        DrawingNumberSource source = sourceResult.Status == PromptStatus.None ||
            string.Equals(sourceResult.StringResult, "L", StringComparison.OrdinalIgnoreCase)
            ? DrawingNumberSource.Layer
            : string.Equals(sourceResult.StringResult, "A", StringComparison.OrdinalIgnoreCase)
            ? DrawingNumberSource.Attribute
            : DrawingNumberSource.Placeholder;

        if (source == DrawingNumberSource.Layer &&
            string.IsNullOrWhiteSpace(_drawingNumberLayer) &&
            !TrySelectDrawingNumberLayer(document))
        {
            return;
        }

        if (source == DrawingNumberSource.Placeholder &&
            !TryPromptPlaceholder(editor))
        {
            return;
        }

        string? attrBlockName = null;
        string attrTag = "图号";

        if (source == DrawingNumberSource.Attribute)
        {
            var entOpts = new PromptEntityOptions("\n选择一个图框块: ")
            {
                AllowNone = false
            };
            entOpts.SetRejectMessage("\n请选择块参照。");
            entOpts.AddAllowedClass(typeof(BlockReference), true);

            PromptEntityResult entRes = editor.GetEntity(entOpts);
            if (entRes.Status != PromptStatus.OK)
                return;

            using (Transaction attrTr =
                document.Database.TransactionManager.StartTransaction())
            {
                BlockReference selBlock = (BlockReference)attrTr.GetObject(
                    entRes.ObjectId, OpenMode.ForRead);
                attrBlockName = selBlock.Name;
                attrTr.Commit();
            }

            var tagOpts = new PromptStringOptions("\n输入要写入的属性标记 <图号>: ")
            {
                AllowSpaces = true
            };
            PromptResult tagResult = editor.GetString(tagOpts);
            if (tagResult.Status != PromptStatus.OK)
                return;
            attrTag = string.IsNullOrWhiteSpace(tagResult.StringResult)
                ? "图号"
                : tagResult.StringResult.Trim();
        }

        PromptPointResult firstCorner = editor.GetPoint("\n指定图纸范围的第一个角点: ");
        if (firstCorner.Status != PromptStatus.OK)
            return;

        var cornerOptions = new PromptCornerOptions(
            "\n指定图纸范围的另一个角点: ", firstCorner.Value);
        PromptPointResult secondCorner = editor.GetCorner(cornerOptions);
        if (secondCorner.Status != PromptStatus.OK)
            return;

        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "TEXT,MTEXT,INSERT")
        });

        Point3d lowerLeft = new(
            Math.Min(firstCorner.Value.X, secondCorner.Value.X),
            Math.Min(firstCorner.Value.Y, secondCorner.Value.Y),
            Math.Min(firstCorner.Value.Z, secondCorner.Value.Z));
        Point3d upperRight = new(
            Math.Max(firstCorner.Value.X, secondCorner.Value.X),
            Math.Max(firstCorner.Value.Y, secondCorner.Value.Y),
            Math.Max(firstCorner.Value.Z, secondCorner.Value.Z));

        PromptSelectionResult selection = editor.SelectCrossingWindow(
            lowerLeft, upperRight, filter);

        if (selection.Status != PromptStatus.OK || selection.Value.Count == 0)
        {
            editor.WriteMessage("\n框选范围内没有可处理的文字或块属性。");
            return;
        }

        var numberOptions = new PromptStringOptions("\n输入起始图号（例如 DB-1）: ")
        {
            AllowSpaces = true
        };
        PromptResult numberResult = editor.GetString(numberOptions);
        if (numberResult.Status != PromptStatus.OK)
            return;

        string drawingNumber = numberResult.StringResult.Trim();
        if (drawingNumber.Length == 0)
        {
            editor.WriteMessage("\n图号不能为空。");
            return;
        }

        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        var frames = new List<FrameTarget>();
        int skipped = 0;

        if (source == DrawingNumberSource.Layer)
        {
            frames = CollectLayerTargets(
                NumberingAttributeSupport.ExpandSelection(
                    selection.Value.GetObjectIds(), transaction),
                _drawingNumberLayer!,
                transaction,
                ref skipped);
        }
        else if (source == DrawingNumberSource.Attribute)
        {
            foreach (ObjectId id in selection.Value.GetObjectIds())
            {
                try
                {
                    BlockReference? block = transaction.GetObject(
                        id, OpenMode.ForRead, false) as BlockReference;
                    if (block is null || block.Name != attrBlockName)
                        continue;

                    foreach (ObjectId attrId in block.AttributeCollection)
                    {
                        if (attrId.IsNull)
                            continue;
                        AttributeReference? attr = transaction.GetObject(
                            attrId, OpenMode.ForRead, false) as AttributeReference;
                        if (attr is null)
                            continue;

                        if (string.Equals(attr.Tag, attrTag,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            frames.Add(new FrameTarget(
                                GetTextPosition(attr),
                                new List<NumberTarget> { new(attr, CanonicalPlaceholder) }));
                            break;
                        }
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    skipped++;
                }
            }
        }
        else
        {
            foreach (ObjectId id in NumberingAttributeSupport.ExpandSelection(
                         selection.Value.GetObjectIds(), transaction))
            {
                try
                {
                    DBObject? value = transaction.GetObject(id, OpenMode.ForRead, false);
                    FrameTarget? frame = value switch
                    {
                        DBText text => CollectTextTarget(
                            text,
                            NumberingAttributeSupport.ReadText(text),
                            GetTextPosition(text),
                            transaction),
                        MText text => CollectTextTarget(
                            text, text.Contents, text.Location, transaction),
                        _ => null
                    };

                    if (frame is not null)
                        frames.Add(frame);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    skipped++;
                }
            }
        }

        if (frames.Count == 0)
        {
            string message = source switch
            {
                DrawingNumberSource.Layer =>
                    $"\n框选范围内未找到图层\u201C{_drawingNumberLayer}\u201D上的普通文字。",
                DrawingNumberSource.Placeholder =>
                    $"\n框选范围内未找到占位符\u201C{_placeholder}\u201D。",
                DrawingNumberSource.Attribute =>
                    $"\n框选范围内未找到块[{attrBlockName}]的属性[{attrTag}]。",
                _ => "\n未找到可处理的对象。"
            };
            editor.WriteMessage(message);
            return;
        }

        NumberingDirection direction = NumberingDirection.LeftToRight;
        if (frames.Count > 1)
        {
            var orderOptions = new PromptKeywordOptions(
                $"\n检测到 {frames.Count} 张图，选择编号方向")
            {
                AllowNone = true,
                AppendKeywordsToMessage = true
            };
            orderOptions.Keywords.Add("L", "L", "左到右(L)");
            orderOptions.Keywords.Add("R", "R", "右到左(R)");
            orderOptions.Keywords.Add("T", "T", "上到下(T)");
            orderOptions.Keywords.Add("B", "B", "下到上(B)");
            orderOptions.Keywords.Default = "L";

            PromptResult orderResult = editor.GetKeywords(orderOptions);
            if (orderResult.Status == PromptStatus.Cancel)
                return;

            string directionValue = orderResult.Status == PromptStatus.None
                ? "L"
                : orderResult.StringResult;
            if (!TryParseDirection(directionValue, out direction))
            {
                editor.WriteMessage("\n方向无效，请输入 L、R、T 或 B。");
                return;
            }
        }

        IEnumerable<FrameTarget> orderedFrames = direction switch
        {
            NumberingDirection.RightToLeft => frames
                .OrderByDescending(frame => frame.Position.Y)
                .ThenByDescending(frame => frame.Position.X),
            NumberingDirection.TopToBottom => frames
                .OrderBy(frame => frame.Position.X)
                .ThenByDescending(frame => frame.Position.Y),
            NumberingDirection.BottomToTop => frames
                .OrderBy(frame => frame.Position.X)
                .ThenBy(frame => frame.Position.Y),
            _ => frames
                .OrderByDescending(frame => frame.Position.Y)
                .ThenBy(frame => frame.Position.X)
        };

        List<FrameTarget> sortedFrames = orderedFrames.ToList();
        SequenceDefinition sequence = ParseSequence(drawingNumber);
        int updated = 0;
        int updatedFrames = 0;
        string firstAssigned = string.Empty;
        string lastAssigned = string.Empty;

        for (int index = 0; index < sortedFrames.Count; index++)
        {
            string assignedNumber = sortedFrames.Count == 1
                ? drawingNumber
                : sequence.GetValue(index);

            if (index == 0)
                firstAssigned = assignedNumber;
            lastAssigned = assignedNumber;

            int frameUpdates = 0;
            foreach (NumberTarget target in sortedFrames[index].Targets)
            {
                try
                {
                    frameUpdates += ApplyText(
                        target.Entity, target.Template, assignedNumber, transaction);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    skipped++;
                }
            }

            if (frameUpdates > 0)
                updatedFrames++;
            updated += frameUpdates;
        }

        if (updated == 0)
        {
            editor.WriteMessage("\n找到图号位置，但对象均不可写。");
            return;
        }

        transaction.Commit();
        editor.Regen();
        if (sortedFrames.Count == 1)
        {
            editor.WriteMessage($"\n已将图号更新为“{drawingNumber}”");
        }
        else
        {
            editor.WriteMessage(
                $"\n已按{GetDirectionName(direction)}顺序更新 {updatedFrames} 张图：" +
                $"{firstAssigned} 至 {lastAssigned}");
        }

        if (skipped > 0)
            editor.WriteMessage($"，另有 {skipped} 个对象因锁定或不可写被跳过");
        editor.WriteMessage("。");
    }

    [CommandMethod("THSZ", CommandFlags.Modal)]
    [CommandMethod("IFX_DRAWINGNO_SETTINGS", CommandFlags.Modal)]
    public void SetPlaceholder()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        TryPromptPlaceholder(document.Editor);
    }

    private static bool TryPromptPlaceholder(Editor editor)
    {
        var options = new PromptStringOptions(
            $"\n输入占位符名称 <{_placeholder}>: ")
        {
            AllowSpaces = true,
            UseDefaultValue = true,
            DefaultValue = _placeholder
        };

        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return false;

        string placeholder = result.StringResult.Trim();
        if (placeholder.Length == 0)
        {
            editor.WriteMessage("\n占位符名称不能为空。");
            return false;
        }

        _placeholder = placeholder;
        editor.WriteMessage($"\n本次使用占位符“{_placeholder}”。");
        return true;
    }

    [CommandMethod("THCS", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void SetDrawingNumberLayer()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        TrySelectDrawingNumberLayer(document);
    }

    [CommandMethod("THBJ", CommandFlags.Modal)]
    public void MarkDrawingNumberAttributes()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        Database database = document.Database;

        // Step 1: select a title block
        var entOpts = new PromptEntityOptions("\n选择一个图框块: ")
        {
            AllowNone = true
        };
        entOpts.SetRejectMessage("\n请选择块参照。");
        entOpts.AddAllowedClass(typeof(BlockReference), true);

        PromptEntityResult entRes = editor.GetEntity(entOpts);
        if (entRes.Status != PromptStatus.OK)
            return;

        using Transaction tr =
            database.TransactionManager.StartTransaction();

        BlockReference selectedBlock = (BlockReference)tr.GetObject(
            entRes.ObjectId, OpenMode.ForRead);
        string blockName = selectedBlock.Name;

        // Step 2: collect attribute tags
        var tags = new List<string>();
        foreach (ObjectId attrId in selectedBlock.AttributeCollection)
        {
            if (attrId.IsNull)
                continue;
            AttributeReference? attr =
                tr.GetObject(attrId, OpenMode.ForRead) as AttributeReference;
            if (attr is not null)
                tags.Add(attr.Tag);
        }

        if (tags.Count == 0)
        {
            editor.WriteMessage("\n该图框块没有属性。");
            return;
        }

        // Step 3: let user pick which tag is the drawing number
        string selectedTag;
        if (tags.Count == 1)
        {
            selectedTag = tags[0];
        }
        else
        {
            var tagOpts = new PromptKeywordOptions(
                "\n选择图号对应的属性标记: ")
            {
                AllowNone = false,
                AppendKeywordsToMessage = true
            };
            foreach (string tag in tags)
            {
                string key = tag.Replace(" ", "_");
                tagOpts.Keywords.Add(key, key, tag);
            }
            tagOpts.Keywords.Default = tags[0].Replace(" ", "_");

            PromptResult tagResult = editor.GetKeywords(tagOpts);
            if (tagResult.Status != PromptStatus.OK)
                return;
            selectedTag = tagResult.StringResult.Replace("_", " ");
        }

        // Step 4: find all same-name blocks, highlight the attribute
        var filter = new SelectionFilter(new TypedValue[]
        {
            new((int)DxfCode.Start, "INSERT"),
            new((int)DxfCode.BlockName, blockName)
        });
        PromptSelectionResult sel = editor.SelectAll(filter);

        if (sel.Status != PromptStatus.OK || sel.Value.Count == 0)
        {
            editor.WriteMessage("\n未找到同名图框块 [" + blockName + "]。");
            return;
        }

        var matchedIds = new List<ObjectId>();

        foreach (ObjectId id in sel.Value.GetObjectIds())
        {
            BlockReference? block =
                tr.GetObject(id, OpenMode.ForRead) as BlockReference;
            if (block is null)
                continue;

            foreach (ObjectId attrId in block.AttributeCollection)
            {
                if (attrId.IsNull)
                    continue;
                AttributeReference? attr =
                    tr.GetObject(attrId, OpenMode.ForRead) as AttributeReference;
                if (attr is null)
                    continue;

                if (string.Equals(attr.Tag, selectedTag,
                    StringComparison.OrdinalIgnoreCase))
                {
                    matchedIds.Add(attrId);
                    editor.WriteMessage(
                        "\n" + attr.Tag + " = " + attr.TextString);
                    break;
                }
            }
        }

        if (matchedIds.Count > 0)
        {
            editor.SetImpliedSelection(matchedIds.ToArray());
            editor.WriteMessage(
                "\n共 " + matchedIds.Count + " 个图框，属性 [" +
                selectedTag + "] 已高亮。");
        }
        else
        {
            editor.WriteMessage(
                "\n未找到属性 [" + selectedTag + "]。");
        }

        tr.Commit();
    }

    private static bool TrySelectDrawingNumberLayer(Document document)
    {
        Editor editor = document.Editor;
        var options = new PromptEntityOptions(
            "\n点选图号位置的普通文字，以指定图号图层: ");
        PromptEntityResult result = editor.GetEntity(options);
        if (result.Status != PromptStatus.OK)
            return false;

        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Entity? entity = transaction.GetObject(
            result.ObjectId, OpenMode.ForRead, false) as Entity;
        if (entity is not DBText && entity is not MText)
        {
            editor.WriteMessage("\n所选对象不是单行文字或多行文字，设置未更改。");
            return false;
        }

        _drawingNumberLayer = entity.Layer;
        editor.WriteMessage(
            $"\n图号图层已指定为“{_drawingNumberLayer}”。" +
            "运行 THBH 并选择“图层”即可批量编号。");
        return true;
    }

    private static List<FrameTarget> CollectLayerTargets(
        IReadOnlyList<ObjectId> objectIds,
        string layerName,
        Transaction transaction,
        ref int skipped)
    {
        var frames = new List<FrameTarget>();
        foreach (ObjectId id in objectIds)
        {
            try
            {
                Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity is null ||
                    !NumberingAttributeSupport.MatchesLayer(
                        entity, layerName, transaction))
                {
                    continue;
                }

                FrameTarget? frame = entity switch
                {
                    DBText text => CreateLayerTextTarget(
                        text,
                        NumberingAttributeSupport.ReadText(text),
                        GetTextPosition(text),
                        transaction),
                    MText text => CreateLayerTextTarget(
                        text, text.Contents, text.Location, transaction),
                    _ => null
                };
                if (frame is not null)
                    frames.Add(frame);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                skipped++;
            }
        }

        return frames;
    }

    private static FrameTarget CreateLayerTextTarget(
        DBObject entity,
        string currentText,
        Point3d position,
        Transaction transaction)
    {
        string template = ReadTemplate(entity, transaction) ??
            CreateTemplateFromExistingDrawingNumber(currentText);
        return new FrameTarget(
            position,
            new List<NumberTarget> { new(entity, template) });
    }

    private static string CreateTemplateFromExistingDrawingNumber(string currentText)
    {
        Match token = DrawingNumberTokenPattern.Match(currentText);
        if (!token.Success)
            return CanonicalPlaceholder;

        return currentText.Substring(0, token.Index) +
            CanonicalPlaceholder +
            currentText.Substring(token.Index + token.Length);
    }

    private static Point3d GetTextPosition(DBText text)
    {
        return text.Justify == AttachmentPoint.BaseLeft
            ? text.Position
            : text.AlignmentPoint;
    }

    private static FrameTarget? CollectTextTarget(
        DBObject entity,
        string currentText,
        Point3d position,
        Transaction transaction)
    {
        NumberTarget? target = CreateNumberTarget(
            entity, currentText, transaction);
        return target is null
            ? null
            : new FrameTarget(position, new List<NumberTarget> { target });
    }

    private static NumberTarget? CreateNumberTarget(
        DBObject entity,
        string currentText,
        Transaction transaction)
    {
        string? template = ReadTemplate(entity, transaction);
        if (template is null)
        {
            if (currentText.IndexOf(_placeholder, StringComparison.Ordinal) >= 0)
            {
                template = ReplaceOrdinal(currentText, _placeholder, CanonicalPlaceholder);
            }
            else
            {
                return null;
            }
        }

        return new NumberTarget(entity, template);
    }

    private static int ApplyText(
        DBObject entity,
        string template,
        string drawingNumber,
        Transaction transaction)
    {
        string updatedText = ReplaceOrdinal(
            template, CanonicalPlaceholder, drawingNumber);

        entity.UpgradeOpen();
        switch (entity)
        {
            case DBText text:
                NumberingAttributeSupport.WriteText(text, updatedText);
                text.AdjustAlignment(text.Database);
                break;
            case MText text:
                text.Contents = updatedText;
                break;
            default:
                return 0;
        }

        SaveTemplate(entity, template, transaction);
        return 1;
    }

    private static SequenceDefinition ParseSequence(string drawingNumber)
    {
        int digitStart = drawingNumber.Length;
        while (digitStart > 0 && char.IsDigit(drawingNumber[digitStart - 1]))
            digitStart--;

        if (digitStart == drawingNumber.Length)
        {
            string separator = drawingNumber.EndsWith("-", StringComparison.Ordinal)
                ? string.Empty
                : "-";
            return new SequenceDefinition(drawingNumber + separator, 1, 1);
        }

        string numberPart = drawingNumber.Substring(digitStart);
        if (!long.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out long start))
            return new SequenceDefinition(drawingNumber + "-", 1, 1);

        return new SequenceDefinition(
            drawingNumber.Substring(0, digitStart),
            start,
            numberPart.Length);
    }

    private static bool TryParseDirection(
        string value,
        out NumberingDirection direction)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "L":
            case "LEFTTORIGHT":
            case "左到右":
            case "从左到右":
                direction = NumberingDirection.LeftToRight;
                return true;
            case "R":
            case "RIGHTTOLEFT":
            case "右到左":
            case "从右到左":
                direction = NumberingDirection.RightToLeft;
                return true;
            case "T":
            case "TOPTOBOTTOM":
            case "上到下":
            case "从上到下":
                direction = NumberingDirection.TopToBottom;
                return true;
            case "B":
            case "BOTTOMTOTOP":
            case "下到上":
            case "从下到上":
                direction = NumberingDirection.BottomToTop;
                return true;
            default:
                direction = NumberingDirection.LeftToRight;
                return false;
        }
    }

    private static string GetDirectionName(NumberingDirection direction)
    {
        return direction switch
        {
            NumberingDirection.RightToLeft => "从右到左",
            NumberingDirection.TopToBottom => "从上到下",
            NumberingDirection.BottomToTop => "从下到上",
            _ => "从左到右"
        };
    }

    private static string? ReadTemplate(DBObject entity, Transaction transaction)
    {
        if (entity.ExtensionDictionary.IsNull)
            return null;

        DBDictionary? dictionary = transaction.GetObject(
            entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
        if (dictionary is null || !dictionary.Contains(MarkerKey))
            return null;

        Xrecord? record = transaction.GetObject(
            dictionary.GetAt(MarkerKey), OpenMode.ForRead, false) as Xrecord;
        TypedValue[]? values = record?.Data?.AsArray();
        return values is { Length: > 0 } ? values[0].Value as string : null;
    }

    private static void SaveTemplate(
        DBObject entity,
        string template,
        Transaction transaction)
    {
        if (entity.ExtensionDictionary.IsNull)
            entity.CreateExtensionDictionary();

        DBDictionary dictionary = (DBDictionary)transaction.GetObject(
            entity.ExtensionDictionary, OpenMode.ForWrite, false);

        if (dictionary.Contains(MarkerKey))
        {
            Xrecord record = (Xrecord)transaction.GetObject(
                dictionary.GetAt(MarkerKey), OpenMode.ForWrite, false);
            record.Data = CreateTemplateData(template);
            return;
        }

        var newRecord = new Xrecord
        {
            Data = CreateTemplateData(template)
        };
        dictionary.SetAt(MarkerKey, newRecord);
        transaction.AddNewlyCreatedDBObject(newRecord, true);
    }

    private static ResultBuffer CreateTemplateData(string template)
    {
        return new ResultBuffer(
            new TypedValue((int)DxfCode.Text, template));
    }

    private static string ReplaceOrdinal(string source, string oldValue, string newValue)
    {
        int index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
            return source;

        var result = new StringBuilder(source.Length);
        int startIndex = 0;
        while (index >= 0)
        {
            result.Append(source, startIndex, index - startIndex);
            result.Append(newValue);
            startIndex = index + oldValue.Length;
            index = source.IndexOf(oldValue, startIndex, StringComparison.Ordinal);
        }

        result.Append(source, startIndex, source.Length - startIndex);
        return result.ToString();
    }

    private sealed class FrameTarget
    {
        public FrameTarget(Point3d position, List<NumberTarget> targets)
        {
            Position = position;
            Targets = targets;
        }

        public Point3d Position { get; }

        public List<NumberTarget> Targets { get; }
    }

    private sealed class NumberTarget
    {
        public NumberTarget(DBObject entity, string template)
        {
            Entity = entity;
            Template = template;
        }

        public DBObject Entity { get; }

        public string Template { get; }
    }

    private sealed class SequenceDefinition
    {
        public SequenceDefinition(string prefix, long start, int width)
        {
            Prefix = prefix;
            Start = start;
            Width = width;
        }

        private string Prefix { get; }

        private long Start { get; }

        private int Width { get; }

        public string GetValue(int offset)
        {
            return Prefix + (Start + offset).ToString(
                "D" + Width, CultureInfo.InvariantCulture);
        }
    }

    private enum NumberingDirection
    {
        LeftToRight,
        RightToLeft,
        TopToBottom,
        BottomToTop
    }

    private enum DrawingNumberSource
    {
        Layer,
        Placeholder,
        Attribute
    }
}
