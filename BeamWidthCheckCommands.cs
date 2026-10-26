using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadDrawingNumber;

public sealed class BeamWidthCheckCommands
{
    private const string MismatchLayer = "梁宽检查-不一致";
    private const string UnresolvedLayer = "梁宽检查-未识别";
    private const double ParallelAngleTolerance = 5.0 * Math.PI / 180.0;

    private static readonly Regex BeamSizePattern = new(
        @"(?<!\d)(\d{2,4}(?:\.\d+)?)\s*[xX×＊*]\s*(\d{2,4}(?:\.\d+)?)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BeamIdPattern = new(
        @"^\s*(?:WKL|KLD|KZL|JZL|KL|LG|LD|TL|XL|DL|L)\s*[-A-Za-z0-9]+(?:\s*\(\s*\d+\s*\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static double _allowedDifference = 2.0;
    private static string _beamLineLayer = "L";
    private static string _beamAnnotationLayer = "Lx";
    private static bool _autoDetectLayers = true;

    [CommandMethod("LWK", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("LJMJS", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("IFX_BEAMWIDTHCHECK", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void CheckBeamWidths()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\n框选需要检查的梁线和梁平法标注: "
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE,TEXT,MTEXT")
        });

        PromptSelectionResult selection = editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK)
            return;

        var toleranceOptions = new PromptDoubleOptions(
            $"\n输入允许误差 <{FormatNumber(_allowedDifference)}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = true,
            DefaultValue = _allowedDifference,
            UseDefaultValue = true
        };
        PromptDoubleResult toleranceResult = editor.GetDouble(toleranceOptions);
        if (toleranceResult.Status == PromptStatus.Cancel)
            return;
        if (toleranceResult.Status == PromptStatus.OK)
            _allowedDifference = toleranceResult.Value;

        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        ObjectId[] selectedIds = selection.Value.GetObjectIds();
        if (_autoDetectLayers && AutoDetectLayers(selectedIds, transaction))
        {
            editor.WriteMessage(
                $"\n已自动识别：梁线层“{_beamLineLayer}”，标注层“{_beamAnnotationLayer}”。");
        }

        var annotations = new List<BeamAnnotation>();
        var beamIds = new List<BeamIdText>();
        var segments = new List<LineSegmentData>();

        foreach (ObjectId id in selectedIds)
        {
            Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            switch (entity)
            {
                case Line line when IsLayer(line, _beamLineLayer):
                    AddSegment(segments, line.StartPoint, line.EndPoint);
                    break;
                case Polyline polyline when IsLayer(polyline, _beamLineLayer):
                    AddPolylineSegments(segments, polyline);
                    break;
                case DBText text when IsLayer(text, _beamAnnotationLayer):
                    Point3d textPosition = text.Justify == AttachmentPoint.BaseLeft
                        ? text.Position
                        : text.AlignmentPoint;
                    AddAnnotation(
                        annotations,
                        text.TextString,
                        textPosition,
                        text.Height,
                        text.Rotation);
                    AddBeamId(
                        beamIds,
                        text.TextString,
                        textPosition,
                        text.Height,
                        text.Rotation);
                    break;
                case MText text when IsLayer(text, _beamAnnotationLayer):
                    AddAnnotation(
                        annotations,
                        text.Contents,
                        text.Location,
                        text.TextHeight,
                        text.Rotation);
                    AddBeamId(
                        beamIds,
                        text.Contents,
                        text.Location,
                        text.TextHeight,
                        text.Rotation);
                    break;
            }
        }

        if (annotations.Count == 0)
        {
            editor.WriteMessage(
                $"\n在标注层“{_beamAnnotationLayer}”中未找到“宽x高”格式的梁平法标注。" +
                "可运行 LJMSZ 重新指定图层。");
            return;
        }

        ObjectId mismatchLayerId = GetOrCreateLayer(
            document.Database, transaction, MismatchLayer, 1);
        ObjectId unresolvedLayerId = GetOrCreateLayer(
            document.Database, transaction, UnresolvedLayer, 2);
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            document.Database.CurrentSpaceId, OpenMode.ForWrite, false);

        int matched = 0;
        int mismatch = 0;
        int unresolved = 0;
        int ignored = 0;

        foreach (BeamAnnotation annotation in annotations)
        {
            if (!TryMeasureBeamWidth(annotation, segments, out double measuredWidth))
            {
                if (HasNearbyBeamId(annotation, beamIds))
                {
                    unresolved++;
                    AddCheckMarker(
                        currentSpace,
                        transaction,
                        annotation,
                        unresolvedLayerId,
                        $"未识别梁边 / 标注{FormatNumber(annotation.DeclaredWidth)}");
                }
                else
                {
                    ignored++;
                }

                continue;
            }

            double difference = Math.Abs(measuredWidth - annotation.DeclaredWidth);
            if (difference <= _allowedDifference)
            {
                matched++;
                continue;
            }

            mismatch++;
            AddCheckMarker(
                currentSpace,
                transaction,
                annotation,
                mismatchLayerId,
                $"标注{FormatNumber(annotation.DeclaredWidth)} / 实测{FormatNumber(measuredWidth)}");
            editor.WriteMessage(
                $"\n[梁宽不一致] {CleanText(annotation.SourceText)}：" +
                $"标注 {FormatNumber(annotation.DeclaredWidth)}，" +
                $"实测 {FormatNumber(measuredWidth)}，" +
                $"差值 {FormatNumber(difference)}");
        }

        transaction.Commit();
        editor.Regen();
        editor.WriteMessage(
            $"\n检查完成：一致 {matched}，不一致 {mismatch}，未识别梁边 {unresolved}，" +
            $"忽略非梁截面文字 {ignored}。允许误差 {FormatNumber(_allowedDifference)}。" +
            $"梁线层“{_beamLineLayer}”，标注层“{_beamAnnotationLayer}”，" +
            $"模式：{(_autoDetectLayers ? "自动识别" : "手动指定")}。");
    }

    [CommandMethod("LJMZD", CommandFlags.Modal)]
    public void EnableAutomaticLayerDetection()
    {
        _autoDetectLayers = true;
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        document?.Editor.WriteMessage(
            "\n梁截面校审已切换为自动识别图层。下次运行 LJMJS 时从框选内容判断。");
    }

    [CommandMethod("LJMSZ", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void SetBeamCheckLayers()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        var lineOptions = new PromptEntityOptions(
            $"\n点选一根梁边线以设置梁线图层 <{_beamLineLayer}>: ");
        PromptEntityResult lineResult = editor.GetEntity(lineOptions);
        if (lineResult.Status != PromptStatus.OK)
            return;

        var textOptions = new PromptEntityOptions(
            $"\n点选一条梁平法文字以设置标注图层 <{_beamAnnotationLayer}>: ");
        PromptEntityResult textResult = editor.GetEntity(textOptions);
        if (textResult.Status != PromptStatus.OK)
            return;

        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Entity? lineEntity = transaction.GetObject(
            lineResult.ObjectId, OpenMode.ForRead, false) as Entity;
        if (lineEntity is not Line && lineEntity is not Polyline)
        {
            editor.WriteMessage("\n第一个对象不是直线或轻量多段线，设置未更改。");
            return;
        }

        Entity? textEntity = transaction.GetObject(
            textResult.ObjectId, OpenMode.ForRead, false) as Entity;
        if (textEntity is not DBText && textEntity is not MText)
        {
            editor.WriteMessage("\n第二个对象不是单行文字或多行文字，设置未更改。");
            return;
        }

        _beamLineLayer = lineEntity.Layer;
        _beamAnnotationLayer = textEntity.Layer;
        _autoDetectLayers = false;
        editor.WriteMessage(
            $"\n梁截面校审已切换为手动指定：梁线“{_beamLineLayer}”，" +
            $"标注“{_beamAnnotationLayer}”。运行 LJMZD 可恢复自动识别。");
    }

    [CommandMethod("LWKSZ", CommandFlags.Modal)]
    public void SetBeamWidthTolerance()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        var options = new PromptDoubleOptions(
            $"\n输入梁宽检查允许误差 <{FormatNumber(_allowedDifference)}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = true,
            DefaultValue = _allowedDifference,
            UseDefaultValue = true
        };
        PromptDoubleResult result = document.Editor.GetDouble(options);
        if (result.Status != PromptStatus.OK)
            return;

        _allowedDifference = result.Value;
        document.Editor.WriteMessage(
            $"\n梁宽检查允许误差已设为 {FormatNumber(_allowedDifference)}。");
    }

    [CommandMethod("LWKQ", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    [CommandMethod("LJMQC", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
    public void ClearBeamWidthMarkers()
    {
        Document? document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        int erased = 0;
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            document.Database.CurrentSpaceId, OpenMode.ForRead, false);

        foreach (ObjectId id in currentSpace)
        {
            Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (entity is null ||
                (!string.Equals(entity.Layer, MismatchLayer, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(entity.Layer, UnresolvedLayer, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
            erased++;
        }

        transaction.Commit();
        document.Editor.Regen();
        document.Editor.WriteMessage($"\n已清除 {erased} 个梁宽检查标记。");
    }

    private static void AddAnnotation(
        ICollection<BeamAnnotation> annotations,
        string sourceText,
        Point3d position,
        double textHeight,
        double rotation)
    {
        Match match = BeamSizePattern.Match(sourceText);
        if (!match.Success ||
            !double.TryParse(
                match.Groups[1].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double width))
        {
            return;
        }

        annotations.Add(new BeamAnnotation(
            sourceText,
            new Point2d(position.X, position.Y),
            Math.Max(textHeight, 1.0),
            NormalizeHalfTurn(rotation),
            width));
    }

    private static bool AutoDetectLayers(
        IReadOnlyList<ObjectId> selectedIds,
        Transaction transaction)
    {
        var textScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (ObjectId id in selectedIds)
        {
            Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            string? sourceText = entity switch
            {
                DBText dbText => dbText.TextString,
                MText mText => mText.Contents,
                _ => null
            };
            if (sourceText is null)
                continue;

            int score = 0;
            if (BeamSizePattern.IsMatch(sourceText))
                score += 5;
            if (BeamIdPattern.IsMatch(CleanText(sourceText).Trim()))
                score += 2;
            if (score == 0)
                continue;

            textScores.TryGetValue(entity!.Layer, out int currentScore);
            textScores[entity.Layer] = currentScore + score;
        }

        string? detectedAnnotationLayer = GetHighestScoreLayer(textScores);
        if (detectedAnnotationLayer is null)
            return false;

        var detectedAnnotations = new List<BeamAnnotation>();
        var segmentsByLayer = new Dictionary<string, List<LineSegmentData>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (ObjectId id in selectedIds)
        {
            Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            switch (entity)
            {
                case DBText text when IsLayer(text, detectedAnnotationLayer):
                    AddAnnotation(
                        detectedAnnotations,
                        text.TextString,
                        text.Justify == AttachmentPoint.BaseLeft
                            ? text.Position
                            : text.AlignmentPoint,
                        text.Height,
                        text.Rotation);
                    break;
                case MText text when IsLayer(text, detectedAnnotationLayer):
                    AddAnnotation(
                        detectedAnnotations,
                        text.Contents,
                        text.Location,
                        text.TextHeight,
                        text.Rotation);
                    break;
                case Line line:
                    AddSegment(
                        GetLayerSegments(segmentsByLayer, line.Layer),
                        line.StartPoint,
                        line.EndPoint);
                    break;
                case Polyline polyline:
                    AddPolylineSegments(
                        GetLayerSegments(segmentsByLayer, polyline.Layer),
                        polyline);
                    break;
            }
        }

        string? detectedLineLayer = null;
        int bestMatchCount = 0;
        double bestDifference = double.MaxValue;
        foreach (KeyValuePair<string, List<LineSegmentData>> pair in segmentsByLayer)
        {
            int matchCount = 0;
            double totalDifference = 0.0;
            foreach (BeamAnnotation annotation in detectedAnnotations)
            {
                if (!TryMeasureBeamWidth(annotation, pair.Value, out double measuredWidth))
                    continue;

                matchCount++;
                totalDifference += Math.Abs(measuredWidth - annotation.DeclaredWidth);
            }

            if (matchCount > bestMatchCount ||
                (matchCount == bestMatchCount && matchCount > 0 && totalDifference < bestDifference))
            {
                bestMatchCount = matchCount;
                bestDifference = totalDifference;
                detectedLineLayer = pair.Key;
            }
        }

        _beamAnnotationLayer = detectedAnnotationLayer;
        if (detectedLineLayer is not null)
            _beamLineLayer = detectedLineLayer;

        return true;
    }

    private static string? GetHighestScoreLayer(IReadOnlyDictionary<string, int> scores)
    {
        string? bestLayer = null;
        int bestScore = 0;
        foreach (KeyValuePair<string, int> pair in scores)
        {
            if (pair.Value <= bestScore)
                continue;

            bestLayer = pair.Key;
            bestScore = pair.Value;
        }

        return bestLayer;
    }

    private static List<LineSegmentData> GetLayerSegments(
        IDictionary<string, List<LineSegmentData>> segmentsByLayer,
        string layerName)
    {
        if (!segmentsByLayer.TryGetValue(layerName, out List<LineSegmentData>? segments))
        {
            segments = new List<LineSegmentData>();
            segmentsByLayer[layerName] = segments;
        }

        return segments;
    }

    private static void AddBeamId(
        ICollection<BeamIdText> beamIds,
        string sourceText,
        Point3d position,
        double textHeight,
        double rotation)
    {
        string cleaned = CleanText(sourceText).Trim();
        if (!BeamIdPattern.IsMatch(cleaned))
            return;

        beamIds.Add(new BeamIdText(
            new Point2d(position.X, position.Y),
            Math.Max(textHeight, 1.0),
            NormalizeHalfTurn(rotation)));
    }

    private static bool HasNearbyBeamId(
        BeamAnnotation annotation,
        IReadOnlyList<BeamIdText> beamIds)
    {
        double searchDistance = Math.Max(annotation.TextHeight * 10.0, 1500.0);
        foreach (BeamIdText beamId in beamIds)
        {
            double allowedDistance = Math.Max(
                searchDistance,
                Math.Max(annotation.TextHeight, beamId.TextHeight) * 10.0);
            if (annotation.Position.GetDistanceTo(beamId.Position) <= allowedDistance &&
                AngleDifference(annotation.Rotation, beamId.Rotation) <= ParallelAngleTolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLayer(Entity entity, string layerName)
    {
        return string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPolylineSegments(
        ICollection<LineSegmentData> segments,
        Polyline polyline)
    {
        int segmentCount = polyline.Closed
            ? polyline.NumberOfVertices
            : polyline.NumberOfVertices - 1;

        for (int index = 0; index < segmentCount; index++)
        {
            if (polyline.GetSegmentType(index) != SegmentType.Line)
                continue;

            int nextIndex = (index + 1) % polyline.NumberOfVertices;
            Point2d start = polyline.GetPoint2dAt(index);
            Point2d end = polyline.GetPoint2dAt(nextIndex);
            AddSegment(
                segments,
                new Point3d(start.X, start.Y, polyline.Elevation),
                new Point3d(end.X, end.Y, polyline.Elevation));
        }
    }

    private static void AddSegment(
        ICollection<LineSegmentData> segments,
        Point3d start,
        Point3d end)
    {
        var value = new LineSegmentData(
            new Point2d(start.X, start.Y),
            new Point2d(end.X, end.Y));
        if (value.Length > 1e-6)
            segments.Add(value);
    }

    private static bool TryMeasureBeamWidth(
        BeamAnnotation annotation,
        IReadOnlyList<LineSegmentData> segments,
        out double measuredWidth)
    {
        measuredWidth = 0.0;
        double bestScore = double.MaxValue;
        double minimumLineLength = Math.Max(annotation.DeclaredWidth * 1.2, 300.0);
        double projectionMargin = Math.Max(annotation.DeclaredWidth, 300.0);

        var nearby = new List<LineSegmentData>();
        foreach (LineSegmentData segment in segments)
        {
            if (segment.Length < minimumLineLength ||
                !segment.IsProjectionNear(annotation.Position, projectionMargin))
            {
                continue;
            }

            double distance = Math.Abs(segment.SignedDistance(annotation.Position));
            if (distance <= Math.Max(annotation.DeclaredWidth * 2.0, 1200.0))
                nearby.Add(segment);
        }

        for (int firstIndex = 0; firstIndex < nearby.Count; firstIndex++)
        {
            LineSegmentData first = nearby[firstIndex];
            for (int secondIndex = firstIndex + 1; secondIndex < nearby.Count; secondIndex++)
            {
                LineSegmentData second = nearby[secondIndex];
                double directionDifference = AngleDifference(first.Angle, second.Angle);
                if (directionDifference > ParallelAngleTolerance)
                    continue;

                double firstDistance = first.SignedDistance(annotation.Position);
                double secondDistance = SignedDistanceUsingDirection(
                    second.Start, annotation.Position, first.Direction);
                double sideTolerance = Math.Max(_allowedDifference, 1.0);
                if (firstDistance * secondDistance > sideTolerance * sideTolerance)
                    continue;

                double width = Math.Abs(firstDistance - secondDistance);
                if (width < 50.0 || width > 3000.0)
                    continue;

                double centerOffset = Math.Abs(Math.Abs(firstDistance) - Math.Abs(secondDistance));
                double textAngleDifference = AngleDifference(first.Angle, annotation.Rotation);
                double score = centerOffset + textAngleDifference * annotation.DeclaredWidth * 0.25;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                measuredWidth = width;
            }
        }

        return bestScore < double.MaxValue;
    }

    private static double SignedDistanceUsingDirection(
        Point2d lineStart,
        Point2d point,
        Vector2d unitDirection)
    {
        Vector2d offset = point - lineStart;
        return unitDirection.X * offset.Y - unitDirection.Y * offset.X;
    }

    private static double AngleDifference(double first, double second)
    {
        double difference = Math.Abs(NormalizeHalfTurn(first) - NormalizeHalfTurn(second));
        return Math.Min(difference, Math.PI - difference);
    }

    private static double NormalizeHalfTurn(double angle)
    {
        double normalized = angle % Math.PI;
        return normalized < 0 ? normalized + Math.PI : normalized;
    }

    private static ObjectId GetOrCreateLayer(
        Database database,
        Transaction transaction,
        string name,
        short colorIndex)
    {
        LayerTable layerTable = (LayerTable)transaction.GetObject(
            database.LayerTableId, OpenMode.ForRead, false);
        if (layerTable.Has(name))
            return layerTable[name];

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
        };
        ObjectId id = layerTable.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    private static void AddCheckMarker(
        BlockTableRecord currentSpace,
        Transaction transaction,
        BeamAnnotation annotation,
        ObjectId layerId,
        string message)
    {
        double radius = Math.Max(annotation.TextHeight * 4.0, 50.0);
        var center = new Point3d(annotation.Position.X, annotation.Position.Y, 0.0);
        var circle = new Circle(center, Vector3d.ZAxis, radius)
        {
            LayerId = layerId
        };
        currentSpace.AppendEntity(circle);
        transaction.AddNewlyCreatedDBObject(circle, true);

        var note = new DBText
        {
            Position = new Point3d(center.X + radius, center.Y + radius, 0.0),
            Height = Math.Max(annotation.TextHeight * 0.8, 2.5),
            TextString = message,
            LayerId = layerId
        };
        currentSpace.AppendEntity(note);
        transaction.AddNewlyCreatedDBObject(note, true);
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string CleanText(string value)
    {
        string cleaned = Regex.Replace(value, @"\\[A-Za-z][^;]*;", string.Empty);
        return cleaned.Length <= 60 ? cleaned : cleaned.Substring(0, 60) + "...";
    }

    private sealed class BeamAnnotation
    {
        public BeamAnnotation(
            string sourceText,
            Point2d position,
            double textHeight,
            double rotation,
            double declaredWidth)
        {
            SourceText = sourceText;
            Position = position;
            TextHeight = textHeight;
            Rotation = rotation;
            DeclaredWidth = declaredWidth;
        }

        public string SourceText { get; }

        public Point2d Position { get; }

        public double TextHeight { get; }

        public double Rotation { get; }

        public double DeclaredWidth { get; }
    }

    private sealed class BeamIdText
    {
        public BeamIdText(Point2d position, double textHeight, double rotation)
        {
            Position = position;
            TextHeight = textHeight;
            Rotation = rotation;
        }

        public Point2d Position { get; }

        public double TextHeight { get; }

        public double Rotation { get; }
    }

    private sealed class LineSegmentData
    {
        public LineSegmentData(Point2d start, Point2d end)
        {
            Start = start;
            End = end;
            Vector2d delta = end - start;
            Length = delta.Length;
            Direction = delta / Length;
            Angle = Math.Atan2(Direction.Y, Direction.X);
        }

        public Point2d Start { get; }

        public Point2d End { get; }

        public double Length { get; }

        public Vector2d Direction { get; }

        public double Angle { get; }

        public double SignedDistance(Point2d point)
        {
            return SignedDistanceUsingDirection(Start, point, Direction);
        }

        public bool IsProjectionNear(Point2d point, double margin)
        {
            Vector2d offset = point - Start;
            double projection = offset.DotProduct(Direction);
            return projection >= -margin && projection <= Length + margin;
        }
    }
}
