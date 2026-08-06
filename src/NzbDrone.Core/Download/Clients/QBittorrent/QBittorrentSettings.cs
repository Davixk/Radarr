using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    public class QBittorrentSettingsValidator : AbstractValidator<QBittorrentSettings>
    {
        public QBittorrentSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());

            RuleFor(c => c.Username).Empty()
                .WithMessage("Username must be empty when using API Key.")
                .When(c => c.ApiKey.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Password).Empty()
                .WithMessage("Password must be empty when using API Key.")
                .When(c => c.ApiKey.IsNotNullOrWhiteSpace());

            RuleFor(c => c.MovieCategory).Matches(@"^([^\\\/](\/?[^\\\/])*)?$").WithMessage(@"Can not contain '\', '//', or start/end with '/'");
            RuleFor(c => c.MovieImportedCategory).Matches(@"^([^\\\/](\/?[^\\\/])*)?$").WithMessage(@"Can not contain '\', '//', or start/end with '/'");
        }
    }

    public class QBittorrentSettings : DownloadClientSettingsBase<QBittorrentSettings>
    {
        private static readonly QBittorrentSettingsValidator Validator = new ();

        public QBittorrentSettings()
        {
            Host = "localhost";
            Port = 8080;
            MovieCategory = "radarr";
            BlocklistOnErroredAsFailed = true;
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "UseSsl", Type = FieldType.Checkbox, HelpText = "DownloadClientQbittorrentSettingsUseSslHelpText")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "UrlBase", Type = FieldType.Textbox, Advanced = true, HelpText = "DownloadClientSettingsUrlBaseHelpText")]
        [FieldToken(TokenField.HelpText, "UrlBase", "clientName", "qBittorrent")]
        [FieldToken(TokenField.HelpText, "UrlBase", "url", "http://[host]:[port]/[urlBase]/api")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "ApiKey", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

        [FieldDefinition(5, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(6, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(7, Label = "Category", Type = FieldType.Textbox, HelpText = "DownloadClientSettingsCategoryHelpText")]
        public string MovieCategory { get; set; }

        [FieldDefinition(8, Label = "PostImportCategory", Type = FieldType.Textbox, Advanced = true, HelpText = "DownloadClientSettingsPostImportCategoryHelpText")]
        public string MovieImportedCategory { get; set; }

        [FieldDefinition(9, Label = "DownloadClientSettingsRecentPriority", Type = FieldType.Select, SelectOptions = typeof(QBittorrentPriority), HelpText = "DownloadClientSettingsRecentPriorityMovieHelpText")]
        public int RecentMoviePriority { get; set; }

        [FieldDefinition(10, Label = "DownloadClientSettingsOlderPriority", Type = FieldType.Select, SelectOptions = typeof(QBittorrentPriority), HelpText = "DownloadClientSettingsOlderPriorityMovieHelpText")]
        public int OlderMoviePriority { get; set; }

        [FieldDefinition(11, Label = "DownloadClientSettingsInitialState", Type = FieldType.Select, SelectOptions = typeof(QBittorrentState), HelpText = "DownloadClientQbittorrentSettingsInitialStateHelpText")]
        public int InitialState { get; set; }

        [FieldDefinition(12, Label = "DownloadClientQbittorrentSettingsSequentialOrder", Type = FieldType.Checkbox, HelpText = "DownloadClientQbittorrentSettingsSequentialOrderHelpText")]
        public bool SequentialOrder { get; set; }

        [FieldDefinition(13, Label = "DownloadClientQbittorrentSettingsFirstAndLastFirst", Type = FieldType.Checkbox, HelpText = "DownloadClientQbittorrentSettingsFirstAndLastFirstHelpText")]
        public bool FirstAndLast { get; set; }

        [FieldDefinition(14, Label = "DownloadClientQbittorrentSettingsContentLayout", Type = FieldType.Select, SelectOptions = typeof(QBittorrentContentLayout), HelpText = "DownloadClientQbittorrentSettingsContentLayoutHelpText")]
        public int ContentLayout { get; set; }

        // fork10: literal Label/HelpText (not localization keys) on purpose. The overlay ships only the rebuilt
        // *.Core/*.Common/*.Api.V3 DLLs, NOT Localization/Core/en.json (loaded from disk), so a key would render
        // as the raw key string; GetLocalizedString falls back to returning the phrase verbatim, so a literal
        // shows correctly with no en.json shipped. Default false preserves upstream behaviour (error -> Warning).
        [FieldDefinition(15, Label = "Report Errored Torrents as Failed", Type = FieldType.Checkbox, Advanced = true, HelpText = "When qBittorrent reports a torrent in the error state, treat it as a failed download so it is blocklisted and re-searched instead of only flagging a warning. The torrent is also removed from the client when Remove is enabled for failed downloads. Off by default; intended for a qBittorrent-compatible client (such as a debrid shim) where 'error' means a terminal failure.")]
        public bool ErrorReportedAsFailed { get; set; }

        // fork11: only meaningful when ErrorReportedAsFailed is on. Default true keeps stock behaviour (a failed
        // download is blocklisted). A missing field in existing clients' settings JSON keeps this ctor default
        // (STJson leaves absent properties at their constructor value), so upgrades stay ON. Literal Label/HelpText
        // for the same overlay reason as ErrorReportedAsFailed (en.json is not shipped).
        [FieldDefinition(16, Label = "Blocklist on Errored-as-Failed", Type = FieldType.Checkbox, Advanced = true, HelpText = "Only applies when 'Report Errored Torrents as Failed' is on. On by default: an errored-as-failed torrent's release is added to the blocklist (stock failed-download behaviour) so it is not grabbed again. Turn off to fail and re-search without blocklisting.")]
        public bool BlocklistOnErroredAsFailed { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
