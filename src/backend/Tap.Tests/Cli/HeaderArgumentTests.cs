using Tap.Studio.Cli.Commands;

namespace Tap.Tests.Cli;

public class HeaderArgumentTests
{
    [Theory]
    [InlineData("Accept: application/json", "Accept", "application/json")]
    [InlineData("X-Trace:abc", "X-Trace", "abc")]
    [InlineData("X-Time: 10:30:00", "X-Time", "10:30:00")]
    [InlineData("X-Empty:", "X-Empty", "")]
    public void Splits_on_the_first_colon(string raw, string name, string value)
    {
        Assert.True(HeaderArgument.TryParse(raw, out var header, out _));
        Assert.Equal(name, header.Key);
        Assert.Equal(value, header.Value);
    }

    [Theory]
    [InlineData("no-colon-here")]
    [InlineData(": value-without-name")]
    [InlineData("")]
    public void Rejects_anything_that_is_not_name_colon_value(string raw)
    {
        Assert.False(HeaderArgument.TryParse(raw, out _, out var error));
        Assert.NotEmpty(error);
    }
}
