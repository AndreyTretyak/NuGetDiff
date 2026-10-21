using Bunit;
using NuGetDiff.Core.Models;
using NuGetDiff.Web.Components;

namespace NuGetDiff.Web.Tests;

public sealed class DiffViewTests : TestContext
{
    [Fact]
    public void Large_diffs_render_in_500_row_chunks()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var diff = BuildDiff(1200);

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, diff)
            .Add(component => component.Language, "csharp"));

        Assert.Equal(500, cut.FindAll("tbody tr").Count);
        Assert.Contains("Showing 500 of 1200 rows", cut.Markup, StringComparison.Ordinal);

        cut.Find(".diff-more button").Click();
        Assert.Equal(1000, cut.FindAll("tbody tr").Count);

        cut.Find(".diff-more button").Click();
        Assert.Equal(1200, cut.FindAll("tbody tr").Count);
        Assert.Empty(cut.FindAll(".diff-more"));
    }

    [Fact]
    public void Small_diffs_render_all_rows_immediately()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var diff = BuildDiff(25);

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, diff));

        Assert.Equal(25, cut.FindAll("tbody tr").Count);
        Assert.Empty(cut.FindAll(".diff-more"));
    }

    [Fact]
    public void Highlights_changed_words_inside_modified_lines()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, BuildModifiedDiff())
            .Add(component => component.Language, "csharp"));

        var deleted = cut.Find(".diff-segment-deleted");
        var inserted = cut.Find(".diff-segment-inserted");
        Assert.Equal("CODE", deleted.TagName);
        Assert.Contains("language-csharp", deleted.ClassList);
        Assert.Equal("brown", deleted.TextContent);
        Assert.Equal("red", inserted.TextContent);
    }

    [Fact]
    public void Highlights_changed_words_without_a_prism_language()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, BuildModifiedDiff()));

        Assert.Equal("SPAN", cut.Find(".diff-segment-deleted").TagName);
        Assert.Equal("SPAN", cut.Find(".diff-segment-inserted").TagName);
    }

    [Fact]
    public void Switches_between_side_by_side_and_inline_layouts()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, BuildModifiedDiff()));

        Assert.Single(cut.FindAll(".diff-table-side-by-side"));

        cut.FindAll(".diff-mode button")
            .Single(button => button.TextContent.Contains("Inline", StringComparison.Ordinal))
            .Click();

        Assert.Empty(cut.FindAll(".diff-table-side-by-side"));
        Assert.Single(cut.FindAll(".diff-table-inline"));
        Assert.Equal(2, cut.FindAll(".diff-table-inline tbody tr").Count);
        Assert.Equal(new[] { "-", "+" }, cut.FindAll(".diff-table-inline td.prefix span[aria-hidden='true']")
            .Select(cell => cell.TextContent)
            .ToArray());
    }

    [Fact]
    public void Renders_unchanged_lines_and_toggles_word_wrap()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var equal = new DiffLine(1, 1, DiffLineKind.Equal, "same");
        var oldChanged = new DiffLine(2, 2, DiffLineKind.Modified, "old");
        var newChanged = new DiffLine(2, 2, DiffLineKind.Modified, "new");
        var diff = new SideBySideDiff(
            new[] { equal, oldChanged },
            new[] { equal, newChanged });
        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, diff)
            .Add(component => component.Language, "text"));

        Assert.Equal(2, cut.FindAll("tbody tr").Count);
        Assert.Contains("same", cut.Find(".diff-view").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input[aria-label='Hide unchanged lines']"));
        Assert.Single(cut.FindAll(".diff-view.wrap-lines"));
        cut.Find("input[aria-label='Wrap lines']").Change(false);
        Assert.Single(cut.FindAll(".diff-view.no-wrap"));
    }

    [Fact]
    public void Preserves_exact_leading_whitespace_in_visible_line_content()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var line = new DiffLine(1, 1, DiffLineKind.Equal, "    same");
        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, new SideBySideDiff(new[] { line }, new[] { line })));

        Assert.All(
            cut.FindAll(".diff-line-content"),
            content => Assert.Equal("    same", content.TextContent));
    }

    [Fact]
    public void Falls_back_to_whole_line_rendering_for_excessive_segments()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var segments = Enumerable.Range(0, 600)
            .Select(index => new DiffSegment(
                DiffSegmentKind.Deleted,
                index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var text = string.Concat(segments.Select(segment => segment.Text));
        var line = new DiffLine(1, 1, DiffLineKind.Modified, text, segments);
        var diff = new SideBySideDiff(new[] { line }, new[] { line });

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, diff)
            .Add(component => component.Language, "text"));

        Assert.Empty(cut.FindAll(".diff-segment"));
        Assert.Equal(2, cut.FindAll("code.language-text").Count);
    }

    [Fact]
    public void Caps_the_total_number_of_visible_word_segments()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var oldLines = Enumerable.Range(1, 10)
            .Select(number => SegmentedLine(number, DiffSegmentKind.Deleted))
            .ToArray();
        var newLines = Enumerable.Range(1, 10)
            .Select(number => SegmentedLine(number, DiffSegmentKind.Inserted))
            .ToArray();

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, new SideBySideDiff(oldLines, newLines))
            .Add(component => component.Language, "text"));

        Assert.InRange(cut.FindAll(".diff-segment").Count, 1, 750);
    }

    [Fact]
    public void Skips_prism_for_a_single_very_large_line()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var text = new string('x', 300_000);
        var line = new DiffLine(1, 1, DiffLineKind.Modified, text);

        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, new SideBySideDiff(new[] { line }, new[] { line }))
            .Add(component => component.Language, "text"));

        Assert.DoesNotContain(
            JSInterop.Invocations,
            invocation => invocation.Identifier == "nuGetDiff.highlightPending");
    }

    [Fact]
    public void Exposes_diff_columns_and_change_states_accessibly()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<DiffView>(parameters => parameters
            .Add(component => component.Diff, BuildModifiedDiff()));

        Assert.Equal(
            new[] { "Old line", "Old content", "New line", "New content" },
            cut.FindAll(".diff-table-side-by-side thead th")
                .Select(header => header.TextContent)
                .ToArray());
        Assert.Contains(
            cut.FindAll(".diff-table-side-by-side td.line"),
            cell => cell.QuerySelector(".sr-only")?.TextContent.Contains(
                "Old content, modified",
                StringComparison.Ordinal) == true
                && cell.TextContent.Contains("brown", StringComparison.Ordinal));
    }

    private static SideBySideDiff BuildDiff(int rows)
    {
        var oldLines = Enumerable.Range(1, rows)
            .Select(number => new DiffLine(number, number, DiffLineKind.Deleted, $"old {number}"))
            .ToArray();
        var newLines = Enumerable.Range(1, rows)
            .Select(number => new DiffLine(number, number, DiffLineKind.Inserted, $"new {number}"))
            .ToArray();
        return new SideBySideDiff(oldLines, newLines);
    }

    private static SideBySideDiff BuildModifiedDiff()
    {
        var oldLine = new DiffLine(
            1,
            1,
            DiffLineKind.Modified,
            "The brown fox",
            new[]
            {
                new DiffSegment(DiffSegmentKind.Equal, "The "),
                new DiffSegment(DiffSegmentKind.Deleted, "brown"),
                new DiffSegment(DiffSegmentKind.Equal, " fox"),
            });
        var newLine = new DiffLine(
            1,
            1,
            DiffLineKind.Modified,
            "The red fox",
            new[]
            {
                new DiffSegment(DiffSegmentKind.Equal, "The "),
                new DiffSegment(DiffSegmentKind.Inserted, "red"),
                new DiffSegment(DiffSegmentKind.Equal, " fox"),
            });
        return new SideBySideDiff(new[] { oldLine }, new[] { newLine });
    }

    private static DiffLine SegmentedLine(int number, DiffSegmentKind kind)
    {
        var segments = Enumerable.Range(0, 100)
            .Select(index => new DiffSegment(kind, index.ToString("D2")))
            .ToArray();
        return new DiffLine(
            number,
            number,
            DiffLineKind.Modified,
            string.Concat(segments.Select(segment => segment.Text)),
            segments);
    }
}
