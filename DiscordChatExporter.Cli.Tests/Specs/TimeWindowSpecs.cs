using System;
using DiscordChatExporter.Cli.Configuration;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class TimeWindowSpecs
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("90m", 0, 0, -90)]
    [InlineData("12h", 0, -12, 0)]
    [InlineData("30d", -30, 0, 0)]
    public void I_can_resolve_a_relative_duration_window(
        string value,
        int days,
        int hours,
        int minutes
    )
    {
        // Act
        var resolved = TimeWindow.Resolve(value, Now);

        // Assert
        resolved.Should().Be(Now.AddDays(days).AddHours(hours).AddMinutes(minutes));
    }

    [Fact]
    public void I_can_resolve_a_week_duration_window()
    {
        // Act
        var resolved = TimeWindow.Resolve("2w", Now);

        // Assert
        resolved.Should().Be(Now.AddDays(-14));
    }

    [Fact]
    public void I_can_resolve_an_absolute_iso_date_window()
    {
        // Act
        var resolved = TimeWindow.Resolve("2026-01-15", Now);

        // Assert
        resolved.Should().Be(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("10")]
    [InlineData("30x")]
    [InlineData("-5d")]
    public void I_cannot_resolve_an_invalid_window(string value)
    {
        // Act & assert
        Assert.Throws<FormatException>(() => TimeWindow.Resolve(value, Now));
    }
}
