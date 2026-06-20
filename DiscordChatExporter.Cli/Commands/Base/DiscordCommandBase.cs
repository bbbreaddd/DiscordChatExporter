using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Cli.Commands.Base;

public abstract class DiscordCommandBase : ICommand
{
    [CommandOption(
        "token",
        't',
        EnvironmentVariable = "DISCORD_TOKEN",
        Description = "Authentication token."
    )]
    public string? Token { get; set; }

    [CommandOption(
        "token-file",
        EnvironmentVariable = "DISCORD_TOKEN_FILE",
        Description = "Path to a file containing additional authentication tokens, one per line. "
            + "If more than one token is available (via this option and/or --token), they will "
            + "be used as fallbacks for one another -- if a request fails because the current "
            + "token is invalid, lacks access, or is being rate limited, the next token will be tried."
    )]
    public string? TokenFile { get; set; }

    [CommandOption(
        "bot",
        'b',
        EnvironmentVariable = "DISCORD_TOKEN_BOT",
        Description = "This option doesn't do anything. Kept for backwards compatibility."
    )]
    public bool IsBotToken { get; set; } = false;

    [CommandOption(
        "respect-rate-limits",
        Description = "Whether to respect advisory rate limits. "
            + "If disabled, only hard rate limits (i.e. 429 responses) will be respected."
    )]
    public bool ShouldRespectRateLimits { get; set; } = true;

    private IReadOnlyList<string> GetTokens()
    {
        var tokens = new List<string>();

        if (!string.IsNullOrWhiteSpace(Token))
            tokens.Add(Token.Trim().Trim('"'));

        if (!string.IsNullOrWhiteSpace(TokenFile))
        {
            if (!File.Exists(TokenFile))
                throw new CommandException($"Token file '{TokenFile}' does not exist.");

            foreach (var line in File.ReadLines(TokenFile))
            {
                var parts = line.Split('#', 2);
                var token = parts[0].Trim().Trim('"');
                if (string.IsNullOrEmpty(token))
                    continue;

                tokens.Add(token);
            }
        }

        if (tokens.Count <= 0)
        {
            throw new CommandException(
                "Missing authentication token. "
                    + "Specify it using the '--token' option or provide a '--token-file'."
            );
        }

        return tokens.Distinct(StringComparer.Ordinal).ToArray();
    }

    [field: AllowNull, MaybeNull]
    protected DiscordClient Discord =>
        field ??= new DiscordClient(
            GetTokens(),
            ShouldRespectRateLimits ? RateLimitPreference.RespectAll : RateLimitPreference.IgnoreAll
        );

    public virtual ValueTask ExecuteAsync(IConsole console)
    {
#pragma warning disable CS0618
        // Warn if the bot option is used
        if (IsBotToken)
        {
            using (console.WithForegroundColor(ConsoleColor.DarkYellow))
            {
                console.Error.WriteLine(
                    "Warning: The --bot option is deprecated and should not be used. "
                        + "The token type is now inferred automatically. "
                        + "Please update your workflows as this option may be completely removed in a future version."
                );
            }
        }
#pragma warning restore CS0618

        // Note about interactivity for Docker
        if (console.IsOutputRedirected && Docker.IsRunningInContainer)
        {
            console.Error.WriteLine(
                "Note: Output streams are redirected, rich console interactions are disabled. "
                    + "If you are running this command in Docker, consider allocating a pseudo-terminal for better user experience (docker run -it ...)."
            );
        }

        return default;
    }
}
