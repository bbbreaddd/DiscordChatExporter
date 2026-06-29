using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal class ExportContext(
    DiscordClient? discord,
    ExportRequest request,
    IReadOnlyDictionary<Snowflake, Member>? members = null,
    IReadOnlyDictionary<Snowflake, Role>? roles = null
)
{
    private readonly Dictionary<Snowflake, Member?> _membersById =
        members?.ToDictionary(kvp => kvp.Key, kvp => (Member?)kvp.Value)
        ?? new Dictionary<Snowflake, Member?>();

    private readonly Dictionary<Snowflake, Channel?> _channelsById = request
        .Channel.GetParents()
        .Append(request.Channel)
        .ToDictionary(c => c.Id, c => (Channel?)c);

    private readonly Dictionary<Snowflake, Role> _rolesById =
        roles?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<Snowflake, Role>();

    private readonly ExportAssetDownloader _assetDownloader = new(
        request.AssetsDirPath,
        request.ShouldReuseAssets,
        request.IsOfflineAssetMode
    );

    private readonly ConcurrentDictionary<string, string> _resolvedAssetUrlsByUrl = new(
        StringComparer.Ordinal
    );

    private readonly Dictionary<string, (string ClassName, int Count)> _avatarClasses = new(
        StringComparer.Ordinal
    );

    public DiscordClient? Discord { get; } = discord;

    public ExportRequest Request { get; } = request;

    public DateTimeOffset NormalizeDate(DateTimeOffset instant) =>
        Request.IsUtcNormalizationEnabled ? instant.ToUniversalTime() : instant.ToLocalTime();

    public string FormatDate(DateTimeOffset instant, string format = "g") =>
        NormalizeDate(instant).ToString(format, Request.CultureInfo);

    public async ValueTask PopulateChannelsAndRolesAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Discord is null)
            return;

        await foreach (
            var channel in Discord.GetGuildChannelsAsync(Request.Guild.Id, cancellationToken)
        )
        {
            _channelsById[channel.Id] = channel;
        }

        await foreach (var role in Discord.GetGuildRolesAsync(Request.Guild.Id, cancellationToken))
        {
            _rolesById[role.Id] = role;
        }
    }

    // Threads are not preloaded, so we resolve them on demand
    public async ValueTask PopulateChannelAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    )
    {
        if (_channelsById.ContainsKey(id))
            return;

        if (Discord is null)
            return;

        var channel = await Discord.TryGetChannelAsync(id, cancellationToken);

        // Store the result even if it's null, to avoid re-fetching non-existing channels
        _channelsById[id] = channel;
    }

    // Because members cannot be pulled in bulk, we need to populate them on demand
    private async ValueTask PopulateMemberAsync(
        Snowflake id,
        User? fallbackUser,
        CancellationToken cancellationToken = default
    )
    {
        if (_membersById.ContainsKey(id))
            return;

        if (Discord is null)
            return;

        var member = await Discord.TryGetGuildMemberAsync(Request.Guild.Id, id, cancellationToken);

        // User may have left the guild since they were mentioned.
        // Create a dummy member object based on the user info.
        if (member is null)
        {
            var user = fallbackUser ?? await Discord.TryGetUserAsync(id, cancellationToken);

            // User may have been deleted since they were mentioned
            if (user is not null)
                member = Member.CreateFallback(user);
        }

        // Store the result even if it's null, to avoid re-fetching non-existing members
        _membersById[id] = member;
    }

    public async ValueTask PopulateMemberAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(id, null, cancellationToken);

    public async ValueTask PopulateMemberAsync(
        User user,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(user.Id, user, cancellationToken);

    public Member? TryGetMember(Snowflake id) => _membersById.GetValueOrDefault(id);

    public Channel? TryGetChannel(Snowflake id) => _channelsById.GetValueOrDefault(id);

    public Role? TryGetRole(Snowflake id) => _rolesById.GetValueOrDefault(id);

    public IReadOnlyList<Role> GetUserRoles(Snowflake id) =>
        TryGetMember(id)
            ?.RoleIds.Select(TryGetRole)
            .WhereNotNull()
            .OrderByDescending(r => r.Position)
            .ToArray()
        ?? [];

    public Color? TryGetUserColor(Snowflake id) =>
        GetUserRoles(id).Where(r => r.Color is not null).Select(r => r.Color).FirstOrDefault();

    private string FormatDownloadedAssetPath(string filePath)
    {
        var relativeFilePath = Path.GetRelativePath(Request.OutputDirPath, filePath);

        // Always prefer a relative path — even one that traverses upward with ".." — because
        // relative paths work correctly when the HTML is served by any web server regardless of
        // mount point. Absolute file:// URIs (the old fallback for out-of-tree asset dirs) are
        // blocked by browsers when the page is loaded over HTTP due to CORS restrictions.
        // For HTML, the path needs to be properly formatted
        return Request.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight
            ? Url.EncodeFilePath(relativeFilePath)
            : relativeFilePath;
    }

    public async ValueTask<string> ResolveAssetUrlAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (_resolvedAssetUrlsByUrl.TryGetValue(url, out var cachedUrl))
            return cachedUrl;

        var resolvedUrl = await ResolveAssetUrlCoreAsync(url, cancellationToken);
        _resolvedAssetUrlsByUrl[url] = resolvedUrl;
        return resolvedUrl;
    }

    private async ValueTask<string> ResolveAssetUrlCoreAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            return url;
        }

        if (!Request.ShouldDownloadAssets && !Request.ShouldCacheAssetsOnly)
            return url;

        try
        {
            var filePath = await _assetDownloader.DownloadAsync(url, cancellationToken);

            // Offline mode (used by 'convert') never downloads: if the asset isn't already cached,
            // there's no local file to point at, so keep the original remote URL.
            if (filePath is null)
                return url;

            // Caching-only mode warms the asset directory as a side effect, but keeps the
            // original (remote) URL in the export so it stays portable and can be safely
            // reused as a source for a later 'convert' run pointed at the same asset directory.
            if (Request.ShouldCacheAssetsOnly)
                return url;

            return FormatDownloadedAssetPath(filePath);
        }
        // Try to catch only exceptions related to failed HTTP requests
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/332
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/372
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // We don't want this to crash the exporting process in case of failure.
            // TODO: add logging so we can be more liberal with catching exceptions.
            return url;
        }
    }

    // In caching-only mode, the export keeps the original remote URL (see ResolveAssetUrlAsync
    // above) but the downloaded copy on disk is otherwise unreferenced. This surfaces that local
    // path separately so it can be recorded alongside the original URL, which is useful because
    // Discord's CDN links for attachments are signed and expire, while the downloaded file does not.
    public async ValueTask<string?> TryGetCachedAssetLocalPathAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (!Request.ShouldCacheAssetsOnly)
            return null;

        try
        {
            var filePath = await _assetDownloader.DownloadAsync(url, cancellationToken);
            return filePath is not null ? FormatDownloadedAssetPath(filePath) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, string> ClassMap = new(StringComparer.Ordinal)
    {
        ["chatlog"] = "cl",
        ["preamble"] = "pe",
        ["preamble__guild-icon-container"] = "pic",
        ["preamble__guild-icon"] = "pi",
        ["preamble__entries-container"] = "ec",
        ["preamble__entry"] = "ee",
        ["preamble__entry--small"] = "ees",
        ["chatlog__message-group"] = "mg",
        ["chatlog__message-container"] = "mc",
        ["chatlog__message-container--pinned"] = "mcp",
        ["chatlog__message-container--highlighted"] = "mch",
        ["chatlog__message"] = "me",
        ["chatlog__message-aside"] = "ma",
        ["chatlog__message-primary"] = "mp",
        ["chatlog__reply-symbol"] = "rs",
        ["chatlog__avatar"] = "av",
        ["chatlog__short-timestamp"] = "st",
        ["chatlog__reply"] = "re",
        ["chatlog__reply-avatar"] = "ra",
        ["chatlog__reply-author"] = "ru",
        ["chatlog__reply-content"] = "rc",
        ["chatlog__reply-link"] = "rl",
        ["chatlog__reply-edited-timestamp"] = "rt",
        ["chatlog__reply-unknown"] = "rn",
        ["chatlog__header"] = "hd",
        ["chatlog__author"] = "au",
        ["chatlog__author-tag"] = "at",
        ["chatlog__timestamp"] = "ts",
        ["chatlog__content"] = "co",
        ["chatlog__markdown"] = "md",
        ["chatlog__markdown-preserve"] = "pr",
        ["chatlog__attachment"] = "ac",
        ["chatlog__attachment--hidden"] = "ach",
        ["chatlog__attachment-media"] = "am",
        ["chatlog__attachment-media--hidden"] = "amh",
        ["chatlog__attachment-media-spoiler"] = "ams",
        ["chatlog__attachment-media-spoiler-label"] = "amsl",
        ["chatlog__attachment-spoiler-caption"] = "asc",
        ["chatlog__attachment-generic"] = "ag",
        ["chatlog__attachment-generic-icon"] = "agi",
        ["chatlog__attachment-generic-name"] = "agn",
        ["chatlog__attachment-generic-size"] = "ags",
        ["chatlog__forwarded-attachments"] = "fa",
        ["chatlog__forwarded-attachment"] = "fm",
        ["chatlog__forwarded"] = "fw",
        ["chatlog__forwarded-header"] = "fwh",
        ["chatlog__forwarded-icon"] = "fwi",
        ["chatlog__forwarded-content"] = "fwc",
        ["chatlog__forwarded-timestamp"] = "fwt",
        ["chatlog__embed-invite-container"] = "eicn",
        ["chatlog__embed-invite-title"] = "eiti",
        ["chatlog__embed-invite"] = "ei",
        ["chatlog__embed-invite-guild-icon-container"] = "eigic",
        ["chatlog__embed-invite-guild-icon"] = "eigi",
        ["chatlog__embed-invite-info"] = "eii",
        ["chatlog__embed-invite-guild-name"] = "eign",
        ["chatlog__embed-invite-channel-name"] = "eicn2",
        ["chatlog__embed-invite-channel-icon"] = "eici",
        ["chatlog__embed-spotify-container"] = "esc",
        ["chatlog__embed-spotify"] = "es",
        ["chatlog__system-notification-icon"] = "si",
        ["chatlog__system-notification-author"] = "sa",
        ["chatlog__system-notification-content"] = "sn",
        ["chatlog__system-notification-link"] = "sl",
        ["chatlog__system-notification-timestamp"] = "sy",
        ["chatlog__markdown-spoiler"] = "sp",
        ["chatlog__markdown-spoiler--hidden"] = "sph",
        ["chatlog__markdown-pre"] = "cp",
        ["chatlog__markdown-pre--inline"] = "cpi",
        ["chatlog__markdown-pre--multiline"] = "cpm",
        ["chatlog__sticker--media"] = "sm",
        ["chatlog__sticker"] = "sk",
        ["chatlog__sticker-media"] = "skm",
        ["chatlog__embed"] = "eb",
        ["chatlog__embed-color-pill"] = "ecp",
        ["chatlog__embed-color-pill--default"] = "ecpd",
        ["chatlog__embed-content-container"] = "ecc",
        ["chatlog__embed-content"] = "eco",
        ["chatlog__embed-text"] = "et",
        ["chatlog__embed-author-container"] = "eac",
        ["chatlog__embed-author-link"] = "eal",
        ["chatlog__embed-author"] = "eau",
        ["chatlog__embed-author-icon"] = "eai",
        ["chatlog__embed-author-name"] = "ean",
        ["chatlog__embed-title"] = "eti",
        ["chatlog__embed-title-link"] = "etl",
        ["chatlog__embed-youtube-container"] = "eyc",
        ["chatlog__embed-youtube-thumbnail"] = "eyt",
        ["chatlog__embed-generic-image"] = "egi",
        ["chatlog__embed-generic-video"] = "egv",
        ["chatlog__embed-generic-gifv"] = "egg",
        ["chatlog__embed-description"] = "ede",
        ["chatlog__embed-fields"] = "efs",
        ["chatlog__embed-field"] = "efd",
        ["chatlog__embed-field--inline"] = "efi",
        ["chatlog__embed-field-name"] = "efn",
        ["chatlog__embed-field-value"] = "efv",
        ["chatlog__embed-image-container"] = "eic",
        ["chatlog__embed-image-link"] = "eil",
        ["chatlog__embed-image"] = "eim",
        ["chatlog__embed-images"] = "eims",
        ["chatlog__embed-images--single"] = "eimss",
        ["chatlog__embed-thumbnail-link"] = "etl3",
        ["chatlog__embed-thumbnail-container"] = "etc",
        ["chatlog__embed-thumbnail"] = "eth",
        ["chatlog__embed-footer"] = "efr",
        ["chatlog__embed-footer-icon"] = "efi",
        ["chatlog__embed-footer-text"] = "eft",
        ["chatlog__reactions"] = "ra",
        ["chatlog__reaction"] = "rc",
        ["chatlog__reaction-count"] = "rct",
        ["chatlog__emoji"] = "ej",
        ["chatlog__emoji--small"] = "ejs",
        ["chatlog__emoji--large"] = "ejl",
        ["chatlog__pagination"] = "pg",
        ["chatlog__pagination-link"] = "pl",
        ["chatlog__pagination-link--disabled"] = "pld",
        ["chatlog__pagination-current"] = "pc",
        ["chatlog__markdown-quote"] = "mq",
        ["chatlog__markdown-quote-border"] = "mqb",
        ["chatlog__markdown-quote-content"] = "mqc",
        ["chatlog__markdown-mention"] = "mn",
        ["chatlog__markdown-timestamp"] = "mt",
        ["postamble"] = "po",
        ["postamble__entry"] = "poe",
    };

    public (string TagType, string ClassName, string SrcOrAria) GetAvatarRenderInfo(
        string resolvedUrl
    )
    {
        if (!Request.IsCompact)
        {
            return (
                "img",
                GetClass("chatlog__avatar"),
                $"src=\"{resolvedUrl}\" alt=\"Avatar\" loading=\"lazy\""
            );
        }

        if (!_avatarClasses.TryGetValue(resolvedUrl, out var entry))
        {
            _avatarClasses[resolvedUrl] = ("a" + _avatarClasses.Count, 1);
            return (
                "img",
                GetClass("chatlog__avatar"),
                $"src=\"{resolvedUrl}\" alt=\"Avatar\" loading=\"lazy\""
            );
        }

        _avatarClasses[resolvedUrl] = (entry.ClassName, entry.Count + 1);
        return (
            "span",
            $"{GetClass("chatlog__avatar")} {entry.ClassName}",
            "aria-label=\"Avatar\" role=\"img\""
        );
    }

    public (string TagType, string ClassName, string SrcOrAria) GetReplyAvatarRenderInfo(
        string resolvedUrl
    )
    {
        if (!Request.IsCompact)
        {
            return (
                "img",
                GetClass("chatlog__reply-avatar"),
                $"src=\"{resolvedUrl}\" alt=\"Avatar\" loading=\"lazy\""
            );
        }

        if (!_avatarClasses.TryGetValue(resolvedUrl, out var entry))
        {
            _avatarClasses[resolvedUrl] = ("a" + _avatarClasses.Count, 1);
            return (
                "img",
                GetClass("chatlog__reply-avatar"),
                $"src=\"{resolvedUrl}\" alt=\"Avatar\" loading=\"lazy\""
            );
        }

        _avatarClasses[resolvedUrl] = (entry.ClassName, entry.Count + 1);
        return (
            "span",
            $"{GetClass("chatlog__reply-avatar")} {entry.ClassName}",
            "aria-label=\"Avatar\" role=\"img\""
        );
    }

    public IReadOnlyDictionary<string, (string ClassName, int Count)> GetAvatarClasses() =>
        _avatarClasses;

    public string GetClass(string fullClassName)
    {
        if (!Request.IsCompact)
            return fullClassName;

        var parts = fullClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (ClassMap.TryGetValue(parts[i], out var shortName))
                parts[i] = shortName;
        }
        return string.Join(' ', parts);
    }

    public string MinifyCss(string css)
    {
        if (!Request.IsCompact)
            return css;

        foreach (var (className, mappedName) in ClassMap)
        {
            css = System.Text.RegularExpressions.Regex.Replace(
                css,
                @"\."
                    + System.Text.RegularExpressions.Regex.Escape(className)
                    + @"(?![a-zA-Z0-9_-])",
                "." + mappedName
            );
        }

        // Strip comments
        css = System.Text.RegularExpressions.Regex.Replace(css, @"/\*[\s\S]*?\*/", "");

        // Remove spaces around symbols: { } : ; ,
        css = System.Text.RegularExpressions.Regex.Replace(css, @"\s*([{};:,])\s*", "$1");

        // Replace consecutive whitespaces with a single space
        css = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");

        return css.Trim();
    }
}
