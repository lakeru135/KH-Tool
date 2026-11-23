using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KhBatchRunner.Export;

/// <summary>
/// The one fix an IFC exported with the Project Base Point as its coordinate base still needs.
///
/// <para><b>Why.</b> With <c>SitePlacement = 2</c> (Project base point) Revit places the site at the
/// PBP, but it still writes the survey offset into the model context's world coordinate system — the
/// <c>IfcCartesianPoint</c> that <c>IFCGEOMETRICREPRESENTATIONCONTEXT(…'Model',3,…)</c> points at
/// through its <c>IfcAxis2Placement3D</c>. Navisworks applies that point, so without this fix the
/// "PBP" file opens back at survey coordinates. Setting it to 0,0,0 leaves the PBP on 0,0,0.</para>
///
/// <para><b>How.</b> Plain text, streamed line by line, so a file of any size costs almost no
/// memory: find the context's axis placement, find that placement's point, check nothing else uses
/// the point, and write a copy with the point set to <c>0.,0.,0.</c>. Line endings and every other
/// byte are kept. Revit writes one STEP entity per line, which is what makes this safe; anything
/// unexpected (no context, a point shared with other entities) is refused rather than guessed
/// at.</para>
///
/// <para>No Revit API here, so it can be checked against any saved IFC without Revit.</para>
/// </summary>
internal static class IfcWcsPatcher
{
    // STEP files are ASCII with \X\ escapes; Latin-1 maps every byte to one char and back, so a
    // read-then-write copy is byte-identical. GetEncoding(28591) rather than Encoding.Latin1, which
    // net48 does not have.
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    private static readonly Regex ContextRe = new(
        @"IFCGEOMETRICREPRESENTATIONCONTEXT\(\$,'Model',3,[^,]+,#(\d+),", RegexOptions.Compiled);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> with the model context's
    /// WCS point set to 0,0,0. Returns the point's original coordinates as written in the file.
    /// Throws, writing nothing at the destination, if the file is not shaped as expected.
    /// </summary>
    public static string Patch(string source, string destination)
    {
        // Pass 1: which IfcAxis2Placement3D the model context uses.
        string? axisId = null;
        foreach (string line in Lines(source))
        {
            Match m = ContextRe.Match(line);
            if (m.Success) { axisId = m.Groups[1].Value; break; }
        }
        if (axisId is null)
            throw new InvalidOperationException("IFC fix: the model's geometric representation context was not found.");

        // Pass 2: which point that placement uses.
        string axisPrefix = "#" + axisId + "=IFCAXIS2PLACEMENT3D(#";
        string? pointId = null;
        foreach (string line in Lines(source))
        {
            if (!line.StartsWith(axisPrefix, StringComparison.Ordinal)) continue;
            string rest = line.Substring(axisPrefix.Length);
            int end = rest.IndexOfAny(new[] { ',', ')' });
            pointId = end > 0 ? rest.Substring(0, end) : null;
            break;
        }
        if (string.IsNullOrEmpty(pointId))
            throw new InvalidOperationException($"IFC fix: the context's placement #{axisId} was not found.");

        // Pass 3: write the copy, counting every use of the point on the way. More than one use means
        // other geometry shares it, and moving it would move that geometry too — refuse.
        var useRe = new Regex("#" + pointId + "(?=[,)])");
        string pointPrefix = "#" + pointId + "=IFCCARTESIANPOINT((";
        string? original = null;
        int uses = 0;
        try
        {
            using (var w = new StreamWriter(destination, false, Latin1))
            {
                foreach (string line in Lines(source))
                {
                    uses += useRe.Matches(line).Count;
                    if (original is null && line.StartsWith(pointPrefix, StringComparison.Ordinal))
                    {
                        string rest = line.Substring(pointPrefix.Length);
                        original = rest.Substring(0, Math.Max(0, rest.IndexOf(')')));
                        string ending = line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
                            : line.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                        w.Write(pointPrefix + "0.,0.,0.));" + ending);
                    }
                    else
                    {
                        w.Write(line);
                    }
                }
            }

            if (original is null)
                throw new InvalidOperationException($"IFC fix: the context's point #{pointId} was not found.");
            if (uses != 1)
                throw new InvalidOperationException(
                    $"IFC fix: the context's point #{pointId} is used by {uses} entities, so it cannot be moved on its own.");
            return original;
        }
        catch
        {
            try { File.Delete(destination); } catch { /* best effort — the caller reports the failure */ }
            throw;
        }
    }

    /// <summary>The file's lines WITH their line endings, so a copy can be byte-identical.</summary>
    private static IEnumerable<string> Lines(string path)
    {
        using var r = new StreamReader(path, Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        var sb = new StringBuilder(256);
        int c;
        while ((c = r.Read()) >= 0)
        {
            sb.Append((char)c);
            if (c == '\n')
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
