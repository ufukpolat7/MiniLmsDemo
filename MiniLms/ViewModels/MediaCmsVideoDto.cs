using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MiniLms.ViewModels
{
    public class MediaCmsApiResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("next")]
        public string? Next { get; set; }

        [JsonPropertyName("previous")]
        public string? Previous { get; set; }

        [JsonPropertyName("results")]
        public List<MediaCmsVideoDto> Results { get; set; } = new();
    }

    public class MediaCmsPlaylistApiResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("results")]
        public List<MediaCmsPlaylistDto> Results { get; set; } = new();
    }

    public class MediaCmsPlaylistDto
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("friendly_token")]
        public string FriendlyToken { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("media_count")]
        public int MediaCount { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("api_url")]
        public string ApiUrl { get; set; } = string.Empty;

        [JsonPropertyName("playlist_media")]
        public List<MediaCmsVideoDto> PlaylistMedia { get; set; } = new();

        public string EmbedUrl => $"http://localhost/playlist/{FriendlyToken}";
    }

    public class MediaCmsCategoryDto
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("is_global")]
        public bool IsGlobal { get; set; }

        [JsonPropertyName("is_lms_course")]
        public bool IsLmsCourse { get; set; }

        [JsonPropertyName("media_count")]
        public int MediaCount { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;
    }

    public class MediaCmsVideoDto
    {
        [JsonPropertyName("friendly_token")]
        public string FriendlyToken { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("thumbnail_url")]
        public string ThumbnailUrl { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("encoding_status")]
        public string EncodingStatus { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public string Size { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        public string EmbedUrl => $"http://localhost/embed?m={FriendlyToken}";
    }
}
