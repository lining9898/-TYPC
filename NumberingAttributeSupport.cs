using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace CadDrawingNumber;

internal static class NumberingAttributeSupport
{
    public static ObjectId[] ExpandSelection(
        ObjectId[] selected,
        Transaction transaction)
    {
        var result = new List<ObjectId>(selected.Length);
        var seen = new HashSet<ObjectId>();
        foreach (ObjectId id in selected)
        {
            if (seen.Add(id))
                result.Add(id);

            BlockReference? block;
            try
            {
                block = transaction.GetObject(
                    id, OpenMode.ForRead, false) as BlockReference;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                continue;
            }

            if (block is null)
                continue;

            foreach (ObjectId attributeId in block.AttributeCollection)
            {
                if (!attributeId.IsNull && seen.Add(attributeId))
                    result.Add(attributeId);
            }
        }

        return result.ToArray();
    }

    public static bool MatchesLayer(
        Entity entity,
        string layer,
        Transaction transaction)
    {
        if (string.Equals(entity.Layer, layer, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entity is not AttributeReference attribute)
            return false;

        try
        {
            BlockReference? owner = transaction.GetObject(
                attribute.OwnerId, OpenMode.ForRead, false) as BlockReference;
            return owner is not null &&
                string.Equals(owner.Layer, layer, StringComparison.OrdinalIgnoreCase);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    public static string ReadText(DBText text)
    {
        if (text is not AttributeReference attribute || !attribute.IsMTextAttribute)
            return text.TextString;

        using MText mtext = attribute.MTextAttribute;
        return mtext?.Contents ?? text.TextString;
    }

    public static void WriteText(DBText text, string value)
    {
        if (text is not AttributeReference attribute || !attribute.IsMTextAttribute)
        {
            text.TextString = value;
            return;
        }

        using MText mtext = attribute.MTextAttribute;
        if (mtext is null)
        {
            text.TextString = value;
            return;
        }

        mtext.Contents = value;
        attribute.MTextAttribute = mtext;
        attribute.UpdateMTextAttribute();
    }
}
