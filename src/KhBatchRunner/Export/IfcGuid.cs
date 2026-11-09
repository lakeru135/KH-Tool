using Autodesk.Revit.DB;

namespace KhBatchRunner.Export;

/// <summary>
/// The 22-character IFC GlobalId Revit's IFC exporter gives an element: the element's stored
/// <c>IfcGUID</c> parameter when it has one, otherwise its export id compressed into IFC's base-64
/// alphabet (the same encoding as the open-source Revit IFC exporter's <c>GUIDUtil</c>).
///
/// <para><b>Informational, not the join key.</b> Measured 2026-11-09 on a sample model, this matched
/// 76 % of elements: railing parts, curtain grids, openings and the like get GUIDs the exporter
/// derives differently. The report joins on Revit ElementId instead, which the exporter writes to
/// <c>IfcElement.Tag</c> (the converter's <c>idmap.csv</c>).</para>
/// </summary>
internal static class IfcGuid
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    public static string For(Document doc, Element e)
    {
        try
        {
            string? stored = e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (!string.IsNullOrWhiteSpace(stored)) return stored!.Trim();
            return Compress(ExportUtils.GetExportId(doc, e.Id));
        }
        catch { return ""; }
    }

    public static string Compress(Guid guid)
    {
        byte[] b = guid.ToByteArray();
        ulong[] n =
        {
            b[3],
            b[2] * 65536UL + b[1] * 256UL + b[0],
            b[5] * 65536UL + b[4] * 256UL + b[7],
            b[6] * 65536UL + b[8] * 256UL + b[9],
            b[10] * 65536UL + b[11] * 256UL + b[12],
            b[13] * 65536UL + b[14] * 256UL + b[15],
        };
        var buf = new char[22];
        int offset = 0;
        for (int i = 0; i < n.Length; i++)
        {
            int len = i == 0 ? 2 : 4;
            for (int j = 0; j < len; j++)
            {
                buf[offset + len - j - 1] = Alphabet[(int)(n[i] % 64)];
                n[i] /= 64;
            }
            offset += len;
        }
        return new string(buf);
    }
}
