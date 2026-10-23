using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using YamlDotNet.RepresentationModel;

namespace MaxPlanner.Infrastructure.Transfer.External;

/// <summary>
/// B-13.3 Markdown records: YAML front matter (YamlDotNet) becomes the record's fields; checkboxes under a "Subtasks" or
/// "Checklist" heading, up to the next same-or-higher-level heading, become checklist items; the rest of the body is
/// kept as notes. Markdig parses the document, so checkboxes inside fenced code blocks are never taken as items.
/// </summary>
internal static partial class MarkdownRecords
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseYamlFrontMatter().UseTaskLists().Build();

    public static IReadOnlyList<ExternalTable> ReadFolder(string folder, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(folder, "*.markdown", SearchOption.AllDirectories))
            .Take(TransferLimits.MaxMarkdownFiles + 1)
            .ToList();
        if (files.Count > TransferLimits.MaxMarkdownFiles)
        {
            throw new TransferFormatException(TransferFailure.TooLarge, "files");
        }

        return ReadFiles(files, folder, cancellationToken);
    }

    public static IReadOnlyList<ExternalTable> ReadFiles(IReadOnlyList<string> files, string root, CancellationToken cancellationToken)
    {
        long total = 0;
        var records = new List<(string Type, string Key, Dictionary<string, ExternalValue> Fields, List<ExternalChecklistItem> Checklist, string? Body)>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += new FileInfo(file).Length;
            if (total > TransferLimits.MaxInputBytes)
            {
                throw new TransferFormatException(TransferFailure.TooLarge, "folder size");
            }

            var key = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = CsvTable.Decode(File.ReadAllBytes(file), key);
            var (fields, checklist, body) = Parse(text, key);
            var type = fields.TryGetValue("type", out var t) ? (t.Text ?? string.Empty).Trim().ToUpperInvariant() : string.Empty;
            records.Add((type, key, fields, checklist, body));
        }

        var tables = new List<ExternalTable>();
        foreach (var group in records.GroupBy(r => EntityOf(r.Type)))
        {
            var columns = new List<string>();
            foreach (var name in group.SelectMany(r => r.Fields.Keys))
            {
                if (!columns.Contains(name, StringComparer.Ordinal))
                {
                    columns.Add(name);
                }
            }

            var tableName = "Markdown › " + group.Key;
            var rows = group.Select((r, i) => new ExternalRow(
                r.Key,
                i + 1,
                [.. columns.Select(c => r.Fields.GetValueOrDefault(c))],
                group.Key == ExternalEntity.Task ? r.Checklist : null,
                r.Body)).ToList();
            tables.Add(new ExternalTable(tableName, ExternalSourceKind.Markdown, group.Key, columns, rows));
        }

        return tables;
    }

    /// <summary>B-13.3: every checkbox outside code blocks of the General checklist file becomes a General to-do.</summary>
    public static IReadOnlyList<ExternalTodo> ReadGeneralChecklist(string file)
    {
        var name = Path.GetFileName(file);
        var text = CsvTable.Decode(TransferFileReader.ReadLimited(file), name);
        var document = Markdown.Parse(text, Pipeline);
        var todos = new List<ExternalTodo>();
        foreach (var item in document.Descendants<ListItemBlock>())
        {
            if (CheckboxOf(item, text) is { } checkbox)
            {
                todos.Add(new ExternalTodo(name + "#" + (todos.Count + 1).ToString(CultureInfo.InvariantCulture), checkbox.Text, checkbox.IsDone));
            }
        }

        return todos;
    }

    private static ExternalEntity EntityOf(string type) => type switch
    {
        "CR" or "CHANGEREQUEST" or "CHANGE-REQUEST" or "CHANGE REQUEST" => ExternalEntity.ChangeRequest,
        "REVIEW" => ExternalEntity.Review,
        "TODO" or "GENERALTODO" => ExternalEntity.GeneralTodo,
        _ => ExternalEntity.Task,
    };

    private static (Dictionary<string, ExternalValue> Fields, List<ExternalChecklistItem> Checklist, string? Body) Parse(string text, string key)
    {
        var document = Markdown.Parse(text, Pipeline);
        var removed = new List<(int Start, int End)>();
        var fields = new Dictionary<string, ExternalValue>(StringComparer.Ordinal);
        if (document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is { } frontMatter)
        {
            removed.Add((frontMatter.Span.Start, frontMatter.Span.End));
            ReadFrontMatter(frontMatter.Lines.ToString(), fields, key);
        }

        var checklist = new List<ExternalChecklistItem>();
        int? sectionLevel = null;
        foreach (var block in document)
        {
            if (block is HeadingBlock heading)
            {
                var title = InlineText(heading.Inline).Trim();
                if (title.Equals("Subtasks", StringComparison.OrdinalIgnoreCase) || title.Equals("Checklist", StringComparison.OrdinalIgnoreCase))
                {
                    sectionLevel = heading.Level;
                    removed.Add((heading.Span.Start, heading.Span.End));
                }
                else if (sectionLevel is { } level && heading.Level <= level)
                {
                    sectionLevel = null;
                }

                continue;
            }

            if (sectionLevel is null || block is not ListBlock list)
            {
                continue;
            }

            foreach (var item in list.Descendants<ListItemBlock>())
            {
                if (CheckboxOf(item, text) is { } checkbox)
                {
                    // Imported checklist text is not duplicated in the editable notes (B-13.3).
                    checklist.Add(checkbox);
                    removed.Add((item.Span.Start, item.Span.End));
                }
            }
        }

        return (fields, checklist, Remaining(text, removed));
    }

    private static ExternalChecklistItem? CheckboxOf(ListItemBlock item, string source)
    {
        if (item.FirstOrDefault() is not ParagraphBlock paragraph || paragraph.Inline?.FirstChild is not TaskList task)
        {
            return null;
        }

        var raw = source.Substring(paragraph.Span.Start, Math.Min(paragraph.Span.Length, source.Length - paragraph.Span.Start));
        var text = Checkbox().Replace(raw, string.Empty).ReplaceLineEndings(" ").Trim();
        return text.Length == 0 ? null : new ExternalChecklistItem(text, task.Checked);
    }

    private static string? Remaining(string text, List<(int Start, int End)> removed)
    {
        var builder = new StringBuilder();
        var position = 0;
        foreach (var (start, end) in removed.OrderBy(r => r.Start))
        {
            if (start > position)
            {
                builder.Append(text, position, start - position);
            }

            position = Math.Max(position, Math.Min(text.Length, end + 1));
        }

        if (position < text.Length)
        {
            builder.Append(text, position, text.Length - position);
        }

        var body = BlankLines().Replace(builder.ToString(), "\n\n").Trim();
        return body.Length == 0 ? null : body;
    }

    private static void ReadFrontMatter(string yaml, Dictionary<string, ExternalValue> fields, string key)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new TransferFormatException(TransferFailure.Malformed, key, ex);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
        {
            return;
        }

        foreach (var (name, value) in mapping.Children)
        {
            if (name is not YamlScalarNode { Value: { } fieldName })
            {
                continue;
            }

            // Only scalars and lists of scalars are fields. Nested mappings are skipped, never expanded, so YAML
            // aliases cannot multiply content.
            switch (value)
            {
                case YamlScalarNode scalar:
                    fields[fieldName] = new ExternalValue(scalar.Value);
                    break;
                case YamlSequenceNode sequence:
                    fields[fieldName] = new ExternalValue(null, [.. sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? string.Empty)]);
                    break;
            }
        }
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var child in inline.Descendants<LiteralInline>())
        {
            builder.Append(child.Content.ToString());
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^\s*\[[ xX]\]\s*", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BlankLines();
}
