using MPCore.Application.Querying;
using MPCore.Application.Results;

namespace MPCore.Application.Tests;

public sealed class PagingTests
{
    [Theory]
    [InlineData(0, 20, 1, 20)]
    [InlineData(-5, 20, 1, 20)]
    [InlineData(3, 0, 3, 1)]
    [InlineData(3, 1000, 3, PageRequest.MaximumSize)]
    [InlineData(2, 50, 2, 50)]
    public void A_page_request_normalises_itself_so_a_query_cannot_receive_a_bad_one(int number, int size, int expectedNumber, int expectedSize)
    {
        var request = new PageRequest(number, size);
        Assert.Equal((expectedNumber, expectedSize), (request.Number, request.Size));
        Assert.Equal((expectedNumber - 1) * expectedSize, request.Skip);
    }

    [Fact]
    public void The_default_request_is_the_first_page_at_the_default_size() =>
        Assert.Equal((1, PageRequest.DefaultSize, 0), (PageRequest.First.Number, PageRequest.First.Size, PageRequest.First.Skip));

    [Theory]
    [InlineData(1, 20, 0, 0, false)]
    [InlineData(1, 20, 45, 3, true)]
    [InlineData(2, 20, 45, 3, true)]
    [InlineData(3, 20, 45, 3, false)]
    public void A_page_reports_how_many_pages_the_total_spans_and_whether_more_follow(int number, int size, long total, int pages, bool more)
    {
        var page = new Page<string>(["a"], number, size, total);
        Assert.Equal((pages, more), (page.PageCount, page.HasMore));
    }

    [Fact]
    public void An_empty_page_keeps_the_shape_of_the_request_that_found_nothing()
    {
        var page = Page<string>.Empty(new PageRequest(4, 25));
        Assert.Empty(page.Items);
        Assert.Equal((4, 25, 0L, 0, false), (page.Number, page.Size, page.Total, page.PageCount, page.HasMore));
    }
}

public sealed class SortAllowlistTests
{
    private static readonly SortAllowlist Allowed = new("title", "createdOnUtc");

    [Fact]
    public void Nothing_requested_means_the_query_keeps_its_own_order()
    {
        var fallback = new SortSpec("createdOnUtc", SortDirection.Descending);
        var resolved = Allowed.Resolve(null, fallback);
        Assert.True(resolved.IsSuccess);
        Assert.Same(fallback, resolved.Value);
    }

    [Fact]
    public void An_allowed_field_comes_back_spelled_the_way_the_query_publishes_it()
    {
        var resolved = Allowed.Resolve(new SortSpec("CREATEDONUTC", SortDirection.Descending), new SortSpec("title"));
        Assert.True(resolved.IsSuccess);
        Assert.Equal(("createdOnUtc", SortDirection.Descending), (resolved.Value.Field, resolved.Value.Direction));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title; drop table categories")]
    [InlineData("")]
    public void Anything_else_is_a_validation_failure_that_names_the_input_without_echoing_it(string field)
    {
        var resolved = Allowed.Resolve(new SortSpec(field), new SortSpec("title"));
        Assert.True(resolved.IsFailure);
        var failure = resolved.FailureDescriptor!;
        Assert.Equal((SortAllowlist.FailureDomain, SortAllowlist.FailureCode, ErrorCategory.Validation),
            (failure.Identity.Domain, failure.Identity.Code, failure.Category));
        var violation = Assert.IsType<ValidationFailureDetail>(Assert.Single(failure.Details)).Violations.Single();
        Assert.Equal("sort", violation.FieldPath);

        var serialised = System.Text.Json.JsonSerializer.Serialize(failure);
        if (field.Length > 0)
        {
            Assert.DoesNotContain(field, serialised, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_allowlist_must_publish_at_least_one_real_field()
    {
        Assert.Throws<ArgumentException>(() => new SortAllowlist());
        Assert.Throws<ArgumentException>(() => new SortAllowlist("title", " "));
    }

    [Fact]
    public void A_sort_can_be_reversed_without_touching_its_field() =>
        Assert.Equal(new SortSpec("title", SortDirection.Descending), new SortSpec("title").Reversed());
}
