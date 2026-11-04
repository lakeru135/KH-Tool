using System.IO;
using System.Xml.Linq;

namespace KhBatchRunner.Interop;

/// <summary>One check cut out of a checkset into its own file, for timing it on its own.</summary>
public sealed record SplitCheck(string Id, string Name, string CheckType, string Heading, bool IsChecked, string Path);

/// <summary>
/// Cuts a Model Checker checkset into one-check checksets, so a profile run can time each check on
/// its own. A whole-checkset run reports only one duration, and "which of 95 checks is slow" can't
/// be answered from that.
///
/// Each file keeps the original root element (name, author, AllowRequired) and the check's own
/// Heading and Section wrappers with their attributes, so AIT sees the check in the same context it
/// has in the full set — including an <c>&lt;--Unused--&gt;</c> heading, which is how a profile run
/// tells whether AIT runs those checks at all. No Revit here, so it can be tested anywhere.
/// </summary>
public static class ChecksetSplitter
{
    public static List<SplitCheck> SplitPerCheck(string checksetPath, string outputFolder)
    {
        XDocument source = XDocument.Load(checksetPath);
        XElement root = source.Root ?? throw new InvalidDataException("Checkset has no root element: " + checksetPath);
        Directory.CreateDirectory(outputFolder);

        var result = new List<SplitCheck>();
        int n = 0;
        foreach (XElement check in root.Descendants("Check"))
        {
            XElement? section = check.Parent;
            XElement? heading = section?.Parent;
            if (section?.Name != "Section" || heading?.Name != "Heading") continue;

            var single = new XElement(root.Name, root.Attributes(),
                new XElement(heading.Name, heading.Attributes(),
                    new XElement(section.Name, section.Attributes(), new XElement(check))));

            string id = (string?)check.Attribute("ID") ?? "";
            string file = System.IO.Path.Combine(outputFolder, $"check_{++n:000}.xml");
            new XDocument(source.Declaration, single).Save(file);

            result.Add(new SplitCheck(
                id,
                (string?)check.Attribute("CheckName") ?? "",
                (string?)check.Attribute("CheckType") ?? "",
                (string?)heading.Attribute("HeadingText") ?? "",
                string.Equals((string?)check.Attribute("IsChecked"), "True", StringComparison.OrdinalIgnoreCase),
                file));
        }
        return result;
    }
}
