using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MiniLms.Interfaces;
using MiniLms.Models;
using MiniLms.ViewModels;

namespace MiniLms.Services
{
    public class MediaCmsService : IMediaCmsService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MediaCmsService> _logger;
        private readonly string _baseUrl;
        private readonly string _apiToken;

        public MediaCmsService(HttpClient httpClient, IConfiguration configuration, ILogger<MediaCmsService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _baseUrl = _configuration["MediaCms:BaseUrl"] ?? "http://localhost";
            _apiToken = _configuration["MediaCms:ApiToken"] ?? string.Empty;
            _httpClient.BaseAddress = new Uri(_baseUrl);
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        private void SetAuthHeader(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(_apiToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiToken);
            }
        }

        public async Task<List<MediaCmsVideoDto>> GetVideosAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media");
                SetAuthHeader(request);
                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var apiResponse = JsonSerializer.Deserialize<MediaCmsApiResponse>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return apiResponse?.Results ?? new List<MediaCmsVideoDto>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching all videos from MediaCMS");
            }

            return new List<MediaCmsVideoDto>();
        }

        public async Task<MediaCmsVideoDto?> GetVideoByTokenAsync(string token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{token}");
                SetAuthHeader(request);
                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<MediaCmsVideoDto>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching video {Token} from MediaCMS", token);
            }

            return null;
        }

        public async Task<MediaCmsCategoryDto?> GetOrCreateCourseCategoryAsync(int courseId, string courseTitle, string courseCode, string? description = null)
        {
            try
            {
                string categoryTitle = $"{courseCode} - {courseTitle}".Trim();
                if (string.IsNullOrWhiteSpace(categoryTitle))
                {
                    categoryTitle = courseTitle;
                }

                var payload = new
                {
                    title = categoryTitle,
                    description = !string.IsNullOrWhiteSpace(description) ? description : $"MiniLMS Dersi: {categoryTitle}"
                };

                using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/categories")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                SetAuthHeader(postRequest);

                var postResponse = await _httpClient.SendAsync(postRequest);
                if (postResponse.IsSuccessStatusCode)
                {
                    var content = await postResponse.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<MediaCmsCategoryDto>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                else
                {
                    _logger.LogWarning("Failed to create/get category in MediaCMS: {StatusCode}", postResponse.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetOrCreateCourseCategoryAsync for course {CourseId}", courseId);
            }

            return null;
        }

        public async Task<bool> AttachVideoToCategoryAsync(string videoToken, string categoryTitle)
        {
            try
            {
                var payload = new
                {
                    action = "add_to_category",
                    media_ids = new[] { videoToken },
                    category = categoryTitle
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/user/bulk_actions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                SetAuthHeader(request);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error attaching video {Token} to category {Category}", videoToken, categoryTitle);
                return false;
            }
        }

        public async Task<MediaCmsPlaylistDto?> GetOrCreateCoursePlaylistAsync(int courseId, string courseTitle, string courseCode, string? teacherName)
        {
            try
            {
                string expectedTitlePrefix = $"[Ders #{courseId}]";

                // 1. Mevcut playlistleri tara
                using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/playlists");
                SetAuthHeader(listRequest);
                var listResponse = await _httpClient.SendAsync(listRequest);

                if (listResponse.IsSuccessStatusCode)
                {
                    var listJson = await listResponse.Content.ReadAsStringAsync();
                    var playlistList = JsonSerializer.Deserialize<MediaCmsPlaylistApiResponse>(listJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    var existing = playlistList?.Results.FirstOrDefault(p => p.Title.StartsWith(expectedTitlePrefix));
                    if (existing != null && !string.IsNullOrEmpty(existing.FriendlyToken))
                    {
                        return await GetPlaylistDetailsAsync(existing.FriendlyToken);
                    }
                }

                // 2. Bulunamadıysa yeni playlist oluştur
                string fullTitle = $"{expectedTitlePrefix} {courseCode} - {courseTitle}".Trim();
                string description = $"Eğitmen: {teacherName ?? "Atanmamış"} | MiniLMS Ders Video Havuzu";

                var payload = new
                {
                    title = fullTitle,
                    description = description
                };

                using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/playlists")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                SetAuthHeader(postRequest);

                var postResponse = await _httpClient.SendAsync(postRequest);
                if (postResponse.IsSuccessStatusCode)
                {
                    var createdJson = await postResponse.Content.ReadAsStringAsync();
                    var created = JsonSerializer.Deserialize<MediaCmsPlaylistDto>(createdJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    _logger.LogInformation("Created new MediaCMS playlist for course {CourseId}: {Token}", courseId, created?.FriendlyToken);
                    return created;
                }
                else
                {
                    _logger.LogWarning("Failed to create playlist in MediaCMS: {StatusCode}", postResponse.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetOrCreateCoursePlaylistAsync for course {CourseId}", courseId);
            }

            return null;
        }

        private async Task<MediaCmsPlaylistDto?> GetPlaylistDetailsAsync(string friendlyToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/playlists/{friendlyToken}");
                SetAuthHeader(request);
                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<MediaCmsPlaylistDto>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching playlist details for {Token}", friendlyToken);
            }

            return null;
        }

        public async Task<List<MediaCmsVideoDto>> GetVideosForCourseAsync(int courseId, string courseTitle, string courseCode, string? teacherName)
        {
            var playlist = await GetOrCreateCoursePlaylistAsync(courseId, courseTitle, courseCode, teacherName);
            if (playlist != null && playlist.PlaylistMedia != null)
            {
                return playlist.PlaylistMedia;
            }

            return new List<MediaCmsVideoDto>();
        }

        public async Task<bool> AddVideoToPlaylistAsync(string playlistToken, string videoToken)
        {
            try
            {
                var payload = new
                {
                    type = "add",
                    media_friendly_token = videoToken
                };

                using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/playlists/{playlistToken}")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                SetAuthHeader(request);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding video {VideoToken} to playlist {PlaylistToken}", videoToken, playlistToken);
                return false;
            }
        }

        public async Task<bool> RemoveVideoFromPlaylistAsync(string playlistToken, string videoToken)
        {
            try
            {
                var payload = new
                {
                    type = "remove",
                    media_friendly_token = videoToken
                };

                using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/playlists/{playlistToken}")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                SetAuthHeader(request);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing video {VideoToken} from playlist {PlaylistToken}", videoToken, playlistToken);
                return false;
            }
        }

        public async Task<MediaCmsVideoDto?> UploadVideoAsync(IFormFile file, string title, string description, string? playlistToken = null, string? categoryTitle = null)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                using var fileStream = file.OpenReadStream();
                using var streamContent = new StreamContent(fileStream);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "video/mp4");

                content.Add(streamContent, "media_file", file.FileName);
                content.Add(new StringContent(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title), "title");
                content.Add(new StringContent(description ?? string.Empty), "description");

                if (!string.IsNullOrWhiteSpace(categoryTitle))
                {
                    content.Add(new StringContent(categoryTitle), "category");
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media")
                {
                    Content = content
                };
                SetAuthHeader(request);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var uploaded = JsonSerializer.Deserialize<MediaCmsVideoDto>(responseBody, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (uploaded != null && !string.IsNullOrEmpty(uploaded.FriendlyToken))
                    {
                        if (!string.IsNullOrEmpty(playlistToken))
                        {
                            await AddVideoToPlaylistAsync(playlistToken, uploaded.FriendlyToken);
                        }
                        if (!string.IsNullOrEmpty(categoryTitle))
                        {
                            await AttachVideoToCategoryAsync(uploaded.FriendlyToken, categoryTitle);
                        }
                    }

                    return uploaded;
                }
                else
                {
                    _logger.LogWarning("MediaCMS video upload failed with status: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading video to MediaCMS");
            }

            return null;
        }

        public async Task SyncAllCoursesAsync(IEnumerable<Course> courses)
        {
            if (courses == null) return;
            foreach (var course in courses)
            {
                string teacherName = course.Teacher != null ? $"{course.Teacher.FirstName} {course.Teacher.LastName}".Trim() : "Eğitmen";
                await GetOrCreateCourseCategoryAsync(course.Id, course.Title, course.CourseCode, course.Description);
                await GetOrCreateCoursePlaylistAsync(course.Id, course.Title, course.CourseCode, teacherName);
            }
        }
    }
}
