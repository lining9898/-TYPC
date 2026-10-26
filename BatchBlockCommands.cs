using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadDrawingNumber;

public sealed class BatchBlockCommands
{
    private static double _clusterDistance = 300.0;
    private static string _blockPrefix = "BLK";
    private static BasePointMode _basePointMode = BasePointMode.LowerLeft;
    private static SourceObjectMode _sourceObjectMode = SourceObjectMode.ConvertToBlock;

    [CommandMethod("BBLK", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("BATCHBLOCK", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void CreateBlocksByCluster()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        Database database = document.Database;

        // ── 1. 框选图元 ──
        var selectedIds = new List<ObjectId>();
        var existing = new HashSet<ObjectId>();

        var selOpts = new PromptSelectionOptions
        {
            MessageForAdding = "\n选择要成块的图元: "
        };
        PromptSelectionResult selection = editor.GetSelection(selOpts);
        if (selection.Status != PromptStatus.OK ||
            selection.Value.Count == 0)
            return;

        foreach (ObjectId id in selection.Value.GetObjectIds())
        {
            if (existing.Add(id))
                selectedIds.Add(id);
        }

        // ── 2. 点选图层过滤（可选，空回车=不筛选） ──
        var layerNames = new List<string>();
        while (true)
        {
            string hint = layerNames.Count == 0
                ? "（已选 0 个图层，直接回车=不筛选）"
                : $"（已选 {layerNames.Count} 个图层，回车=结束）";

            var entOpts = new PromptEntityOptions(
                $"\n点选参照图元取其图层 [列出图层(L)/输入名称(N)] {hint}: ")
            {
                AllowNone = true
            };
            entOpts.Keywords.Add("L", "L", "列出图层(L)");
            entOpts.Keywords.Add("N", "N", "输入名称(N)");

            PromptEntityResult entRes = editor.GetEntity(entOpts);
            if (entRes.Status == PromptStatus.None ||
                entRes.Status == PromptStatus.Cancel)
                break;

            if (entRes.Status == PromptStatus.Keyword)
            {
                if (entRes.StringResult == "L")
                {
                    using Transaction lt =
                        database.TransactionManager.StartTransaction();
                    var layerTable = (LayerTable)lt.GetObject(
                        database.LayerTableId, OpenMode.ForRead);
                    int count = 0;
                    foreach (ObjectId lid in layerTable)
                    {
                        var layer = (LayerTableRecord)lt.GetObject(
                            lid, OpenMode.ForRead);
                        editor.WriteMessage("\n  " + layer.Name);
                        count++;
                    }
                    lt.Commit();
                    editor.WriteMessage($"\n  共 {count} 个图层");
                    continue;
                }
                else if (entRes.StringResult == "N")
                {
                    var nameOpts = new PromptStringOptions(
                        "\n输入图层名（多个用逗号分隔，支持 * 通配）: ")
                    {
                        AllowSpaces = true
                    };
                    PromptResult nameResult = editor.GetString(nameOpts);
                    if (nameResult.Status != PromptStatus.OK)
                        continue;
                    string[] names = nameResult.StringResult.Split(
                        ',', '，', ';');
                    foreach (string raw in names)
                    {
                        string name = raw.Trim();
                        if (name.Length == 0) continue;
                        if (!layerNames.Contains(name))
                        {
                            layerNames.Add(name);
                            editor.WriteMessage(
                                $"\n  已添加图层: {name}" +
                                $"（当前 {layerNames.Count} 个）");
                        }
                        else
                        {
                            editor.WriteMessage(
                                $"\n  图层已在列表中: {name}");
                        }
                    }
                    continue;
                }
            }

            if (entRes.Status != PromptStatus.OK)
                break;

            using Transaction lt2 =
                database.TransactionManager.StartTransaction();
            Entity ent = (Entity)lt2.GetObject(
                entRes.ObjectId, OpenMode.ForRead);
            string layerName = ent.Layer;
            lt2.Commit();

            if (!layerNames.Contains(layerName))
            {
                layerNames.Add(layerName);
                editor.WriteMessage(
                    $"\n  已添加图层: {layerName}" +
                    $"（当前 {layerNames.Count} 个）");
            }
            else
            {
                editor.WriteMessage(
                    $"\n  图层已在列表中: {layerName}");
            }
        }

        // 按图层过滤并高亮
        if (layerNames.Count > 0)
        {
            var filtered = new List<ObjectId>();
            using Transaction ft =
                database.TransactionManager.StartTransaction();
            foreach (ObjectId id in selectedIds)
            {
                Entity ent = (Entity)ft.GetObject(id, OpenMode.ForRead);
                bool match = false;
                foreach (string pattern in layerNames)
                {
                    if (LayerWildcardMatch(ent.Layer, pattern))
                    {
                        match = true;
                        break;
                    }
                }
                if (match)
                    filtered.Add(id);
            }
            ft.Commit();
            editor.WriteMessage(
                $"\n已高亮 {filtered.Count} 个该图层的图元");
            editor.SetImpliedSelection(filtered.ToArray());
            selectedIds = filtered;
        }
        else
        {
            editor.SetImpliedSelection(selectedIds.ToArray());
        }

        // 清除高亮
        editor.SetImpliedSelection((ObjectId[]?)null);

        if (selectedIds.Count == 0)
            return;

        if (!PromptSettings(editor))
            return;

        try
        {
            using Transaction transaction =
                database.TransactionManager.StartTransaction();
            List<EntityBox> boxes = CollectEntityBoxes(
                selectedIds, transaction);
            if (boxes.Count == 0)
            {
                editor.WriteMessage("\n所选对象均无法获取有效包围盒。");
                return;
            }

            List<List<EntityBox>> groups = Cluster(boxes, _clusterDistance);
            if (groups.Count == 0)
            {
                editor.WriteMessage("\n未识别到可创建图块的对象组。");
                return;
            }

            BlockTable blockTable = (BlockTable)transaction.GetObject(
                database.BlockTableId, OpenMode.ForRead, false);
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId, OpenMode.ForWrite, false);

            int created = 0;
            int skipped = 0;
            foreach (List<EntityBox> group in groups)
            {
                if (_sourceObjectMode == SourceObjectMode.ConvertToBlock &&
                    ContainsLockedEntity(group, transaction))
                {
                    skipped++;
                    continue;
                }

                Point3d basePoint = GetBasePoint(group, _basePointMode);
                string blockName = GetUniqueBlockName(blockTable, _blockPrefix);
                ObjectId blockId = CreateBlockDefinition(
                    blockTable,
                    group,
                    basePoint,
                    blockName,
                    transaction);

                if (_sourceObjectMode == SourceObjectMode.ConvertToBlock)
                {
                    foreach (EntityBox item in group)
                    {
                        Entity entity = (Entity)transaction.GetObject(
                            item.EntityId, OpenMode.ForWrite, false);
                        entity.Erase();
                    }

                    var reference = new BlockReference(basePoint, blockId);
                    currentSpace.AppendEntity(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                    AddAttributeReferences(reference, transaction);
                }

                created++;
            }

            transaction.Commit();
            editor.Regen();
            editor.WriteMessage(
                $"\n批量做图块完成：识别 {groups.Count} 堆，创建 {created} 个图块，" +
                $"块名前缀“{_blockPrefix}”。");
            if (skipped > 0)
                editor.WriteMessage($" 跳过 {skipped} 堆含锁定图层对象的图形。");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        {
            editor.WriteMessage($"\n批量做图块失败：{exception.Message}");
        }
    }

    [CommandMethod("SELBLK", CommandFlags.Modal)]
    public void SelectSameNameBlocks()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        Database database = document.Database;

        var entOpts = new PromptEntityOptions("\n选择一个图块参照: ")
        {
            AllowNone = true
        };
        entOpts.SetRejectMessage("\n请选择图块参照。");
        entOpts.AddAllowedClass(typeof(BlockReference), true);

        PromptEntityResult entRes = editor.GetEntity(entOpts);
        if (entRes.Status != PromptStatus.OK)
            return;

        using Transaction tr =
            database.TransactionManager.StartTransaction();
        BlockReference refBlock = (BlockReference)tr.GetObject(
            entRes.ObjectId, OpenMode.ForRead);
        string blockName = refBlock.Name;
        tr.Commit();

        TypedValue[] filterValues =
        {
            new((int)DxfCode.Start, "INSERT"),
            new((int)DxfCode.BlockName, blockName)
        };
        var filter = new SelectionFilter(filterValues);
        PromptSelectionResult sel = editor.SelectAll(filter);

        if (sel.Status == PromptStatus.OK && sel.Value.Count > 0)
        {
            ObjectId[] ids = sel.Value.GetObjectIds();
            editor.SetImpliedSelection(ids);
            editor.WriteMessage(
                "\n已选择 " + ids.Length + " 个同名图块 [" + blockName + "]。");
        }
        else
        {
            editor.WriteMessage(
                "\n未找到同名图块 [" + blockName + "]。");
        }
    }

    /// <summary>
    /// 图层名通配匹配，支持 * 和 ?。
    /// </summary>
    private static bool LayerWildcardMatch(string layerName, string pattern)
    {
        if (pattern == "*" || pattern.Length == 0)
            return true;
        // 精确匹配
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(layerName, pattern,
                StringComparison.OrdinalIgnoreCase);
        // 转换为正则
        string regex = "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            layerName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool PromptSettings(Editor editor)
    {
        var distanceOptions = new PromptDistanceOptions(
            $"\n聚簇距离（间隙不大于此值归为一块） <{FormatNumber(_clusterDistance)}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = _clusterDistance,
            UseDefaultValue = true
        };
        PromptDoubleResult distanceResult = editor.GetDistance(distanceOptions);
        if (distanceResult.Status == PromptStatus.Cancel)
            return false;
        if (distanceResult.Status == PromptStatus.OK)
            _clusterDistance = distanceResult.Value;

        var prefixOptions = new PromptStringOptions(
            $"\n输入图块名前缀 <{_blockPrefix}>: ")
        {
            AllowSpaces = false,
            DefaultValue = _blockPrefix,
            UseDefaultValue = true
        };
        PromptResult prefixResult = editor.GetString(prefixOptions);
        if (prefixResult.Status != PromptStatus.OK)
            return false;

        string prefix = prefixResult.StringResult.Trim();
        if (prefix.Length == 0 || !IsValidBlockPrefix(prefix))
        {
            editor.WriteMessage("\n图块名前缀无效，请勿使用空格或块名禁用字符。");
            return false;
        }
        _blockPrefix = prefix;

        var basePointOptions = new PromptKeywordOptions(
            "\n选择块基点 [左下角(L)/中心(C)] <左下角>: ")
        {
            AllowNone = true,
            AppendKeywordsToMessage = false
        };
        basePointOptions.Keywords.Add("L", "L", "左下角(L)");
        basePointOptions.Keywords.Add("C", "C", "中心(C)");
        basePointOptions.Keywords.Default = _basePointMode == BasePointMode.Center ? "C" : "L";
        PromptResult basePointResult = editor.GetKeywords(basePointOptions);
        if (basePointResult.Status == PromptStatus.Cancel)
            return false;
        if (basePointResult.Status == PromptStatus.OK)
        {
            _basePointMode = string.Equals(
                basePointResult.StringResult, "C", StringComparison.OrdinalIgnoreCase)
                ? BasePointMode.Center
                : BasePointMode.LowerLeft;
        }

        var sourceOptions = new PromptKeywordOptions(
            "\n原对象处理 [转成块(T)/仅建块定义(K)] <转成块>: ")
        {
            AllowNone = true,
            AppendKeywordsToMessage = false
        };
        sourceOptions.Keywords.Add("T", "T", "转成块(T)");
        sourceOptions.Keywords.Add("K", "K", "仅建块定义(K)");
        sourceOptions.Keywords.Default =
            _sourceObjectMode == SourceObjectMode.KeepOriginals ? "K" : "T";
        PromptResult sourceResult = editor.GetKeywords(sourceOptions);
        if (sourceResult.Status == PromptStatus.Cancel)
            return false;
        if (sourceResult.Status == PromptStatus.OK)
        {
            _sourceObjectMode = string.Equals(
                sourceResult.StringResult, "K", StringComparison.OrdinalIgnoreCase)
                ? SourceObjectMode.KeepOriginals
                : SourceObjectMode.ConvertToBlock;
        }

        return true;
    }

    private static List<EntityBox> CollectEntityBoxes(
        IReadOnlyList<ObjectId> ids,
        Transaction transaction)
    {
        var boxes = new List<EntityBox>();
        foreach (ObjectId id in ids)
        {
            try
            {
                Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity is null || entity.IsErased)
                    continue;

                Extents3d extents = entity.GeometricExtents;
                boxes.Add(new EntityBox(id, extents.MinPoint, extents.MaxPoint));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }

        return boxes;
    }

    private static List<List<EntityBox>> Cluster(
        IReadOnlyList<EntityBox> boxes,
        double tolerance)
    {
        int count = boxes.Count;
        var parent = new int[count];
        for (int index = 0; index < count; index++)
            parent[index] = index;

        int[] sorted = Enumerable.Range(0, count)
            .OrderBy(index => boxes[index].Minimum.X)
            .ToArray();
        double expansion = tolerance / 2.0;

        for (int sortedIndex = 0; sortedIndex < count; sortedIndex++)
        {
            int firstIndex = sorted[sortedIndex];
            EntityBox first = boxes[firstIndex];
            for (int next = sortedIndex + 1; next < count; next++)
            {
                int secondIndex = sorted[next];
                EntityBox second = boxes[secondIndex];
                if (second.Minimum.X - first.Maximum.X > tolerance)
                    break;

                if (Touches(first, second, expansion))
                    Union(parent, firstIndex, secondIndex);
            }
        }

        var byRoot = new Dictionary<int, List<EntityBox>>();
        for (int index = 0; index < count; index++)
        {
            int root = Find(parent, index);
            if (!byRoot.TryGetValue(root, out List<EntityBox>? group))
            {
                group = new List<EntityBox>();
                byRoot.Add(root, group);
            }
            group.Add(boxes[index]);
        }

        return byRoot.Values
            .OrderByDescending(group => group.Max(item => item.Maximum.Y))
            .ThenBy(group => group.Min(item => item.Minimum.X))
            .ToList();
    }

    private static bool Touches(EntityBox first, EntityBox second, double expansion)
    {
        return first.Maximum.X + expansion >= second.Minimum.X - expansion &&
            second.Maximum.X + expansion >= first.Minimum.X - expansion &&
            first.Maximum.Y + expansion >= second.Minimum.Y - expansion &&
            second.Maximum.Y + expansion >= first.Minimum.Y - expansion;
    }

    private static int Find(int[] parent, int value)
    {
        int root = value;
        while (parent[root] != root)
            root = parent[root];

        while (parent[value] != value)
        {
            int next = parent[value];
            parent[value] = root;
            value = next;
        }
        return root;
    }

    private static void Union(int[] parent, int first, int second)
    {
        int firstRoot = Find(parent, first);
        int secondRoot = Find(parent, second);
        if (firstRoot != secondRoot)
            parent[secondRoot] = firstRoot;
    }

    private static Point3d GetBasePoint(
        IReadOnlyList<EntityBox> group,
        BasePointMode mode)
    {
        double minimumX = group.Min(item => item.Minimum.X);
        double minimumY = group.Min(item => item.Minimum.Y);
        double maximumX = group.Max(item => item.Maximum.X);
        double maximumY = group.Max(item => item.Maximum.Y);

        return mode == BasePointMode.Center
            ? new Point3d(
                (minimumX + maximumX) / 2.0,
                (minimumY + maximumY) / 2.0,
                0.0)
            : new Point3d(minimumX, minimumY, 0.0);
    }

    private static bool ContainsLockedEntity(
        IEnumerable<EntityBox> group,
        Transaction transaction)
    {
        foreach (EntityBox item in group)
        {
            Entity entity = (Entity)transaction.GetObject(
                item.EntityId, OpenMode.ForRead, false);
            LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
                entity.LayerId, OpenMode.ForRead, false);
            if (layer.IsLocked)
                return true;
        }
        return false;
    }

    private static ObjectId CreateBlockDefinition(
        BlockTable blockTable,
        IReadOnlyList<EntityBox> group,
        Point3d basePoint,
        string blockName,
        Transaction transaction)
    {
        if (!blockTable.IsWriteEnabled)
            blockTable.UpgradeOpen();

        var definition = new BlockTableRecord
        {
            Name = blockName,
            Origin = Point3d.Origin
        };
        ObjectId blockId = blockTable.Add(definition);
        transaction.AddNewlyCreatedDBObject(definition, true);

        Matrix3d toLocal = Matrix3d.Displacement(Point3d.Origin - basePoint);
        foreach (EntityBox item in group)
        {
            Entity original = (Entity)transaction.GetObject(
                item.EntityId, OpenMode.ForRead, false);
            var clone = (Entity)original.Clone();
            clone.TransformBy(toLocal);
            definition.AppendEntity(clone);
            transaction.AddNewlyCreatedDBObject(clone, true);
        }

        return blockId;
    }

    private static void AddAttributeReferences(
        BlockReference reference,
        Transaction transaction)
    {
        BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
            reference.BlockTableRecord, OpenMode.ForRead, false);
        foreach (ObjectId id in definition)
        {
            AttributeDefinition? definitionAttribute = transaction.GetObject(
                id, OpenMode.ForRead, false) as AttributeDefinition;
            if (definitionAttribute is null || definitionAttribute.Constant)
                continue;

            var referenceAttribute = new AttributeReference();
            referenceAttribute.SetAttributeFromBlock(
                definitionAttribute, reference.BlockTransform);
            referenceAttribute.TextString = definitionAttribute.TextString;
            reference.AttributeCollection.AppendAttribute(referenceAttribute);
            transaction.AddNewlyCreatedDBObject(referenceAttribute, true);
        }
    }

    private static string GetUniqueBlockName(BlockTable blockTable, string prefix)
    {
        int number = 1;
        string name;
        do
        {
            name = prefix + "-" + number.ToString("D3", CultureInfo.InvariantCulture);
            number++;
        }
        while (blockTable.Has(name));

        return name;
    }

    private static bool IsValidBlockPrefix(string prefix)
    {
        try
        {
            SymbolUtilityServices.ValidateSymbolName(prefix + "-001", false);
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private sealed class EntityBox
    {
        public EntityBox(ObjectId entityId, Point3d minimum, Point3d maximum)
        {
            EntityId = entityId;
            Minimum = minimum;
            Maximum = maximum;
        }

        public ObjectId EntityId { get; }

        public Point3d Minimum { get; }

        public Point3d Maximum { get; }
    }

    private enum BasePointMode
    {
        LowerLeft,
        Center
    }

    private enum SourceObjectMode
    {
        ConvertToBlock,
        KeepOriginals
    }
}
