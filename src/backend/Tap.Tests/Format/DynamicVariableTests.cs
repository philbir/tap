using System.Globalization;
using Tap.Workspace.Rendering;

namespace Tap.Tests.Format;

/// <summary>
/// The generated <c>{{$…}}</c> tokens. Deliberately a small set: each tool in the .http
/// ecosystem ships a different and larger one, and inventing values for tokens another tool
/// defines differently would be worse than reporting them as unknown.
/// </summary>
public class DynamicVariableTests
{
    [Theory]
    [InlineData("$guid")]
    [InlineData("$uuid")]
    public void Guid_tokens_produce_a_parseable_guid(string token)
    {
        Assert.True(DynamicVariables.TryResolve(token, out var value));
        Assert.True(Guid.TryParse(value, out _));
    }

    [Fact]
    public void Timestamp_is_unix_seconds()
    {
        Assert.True(DynamicVariables.TryResolve("$timestamp", out var value));
        var seconds = long.Parse(value, CultureInfo.InvariantCulture);
        var when = DateTimeOffset.FromUnixTimeSeconds(seconds);
        Assert.True((DateTimeOffset.UtcNow - when).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void IsoTimestamp_round_trips_as_utc()
    {
        Assert.True(DynamicVariables.TryResolve("$isoTimestamp", out var value));
        Assert.EndsWith("Z", value, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out _));
    }

    [Fact]
    public void RandomInt_defaults_to_the_ecosystem_range()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.True(DynamicVariables.TryResolve("$randomInt", out var value));
            var n = int.Parse(value, CultureInfo.InvariantCulture);
            Assert.InRange(n, 0, 999);
        }
    }

    [Fact]
    public void RandomInt_honours_explicit_bounds()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.True(DynamicVariables.TryResolve("$randomInt 10 20", out var value));
            Assert.InRange(int.Parse(value, CultureInfo.InvariantCulture), 10, 19);
        }
    }

    [Fact]
    public void Malformed_bounds_fall_back_rather_than_failing()
    {
        // A bad token is not worth blocking a request over.
        Assert.True(DynamicVariables.TryResolve("$randomInt banana", out var value));
        Assert.InRange(int.Parse(value, CultureInfo.InvariantCulture), 0, 999);
    }

    [Theory]
    [InlineData("$datetime")]      // REST Client has it; we don't, and its format args differ.
    [InlineData("$processEnv")]    // Would be a credential-exfiltration primitive.
    [InlineData("$random")]
    [InlineData("notdynamic")]
    public void Unknown_tokens_are_left_for_normal_resolution(string name)
    {
        Assert.False(DynamicVariables.TryResolve(name, out _));
        Assert.False(DynamicVariables.IsDynamic(name));
    }

    [Fact]
    public void Dynamic_tokens_are_not_reported_as_variables_the_user_must_supply()
    {
        var names = Interpolation.ReferencedNames("{{$guid}} {{realVar}} {{$timestamp}}");
        Assert.Equal(["realVar"], names);
    }
}
