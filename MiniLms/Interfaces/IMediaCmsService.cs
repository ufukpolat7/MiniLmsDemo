using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using MiniLms.Models;
using MiniLms.ViewModels;

namespace MiniLms.Interfaces
{
    public interface IMediaCmsService
    {
        Task<List<MediaCmsVideoDto>> GetVideosAsync();
        Task<MediaCmsVideoDto?> GetVideoByTokenAsync(string token);
        Task<MediaCmsVideoDto?> UploadVideoAsync(IFormFile file, string title, string description, string? playlistToken = null, string? categoryTitle = null);
        Task<MediaCmsPlaylistDto?> GetOrCreateCoursePlaylistAsync(int courseId, string courseTitle, string courseCode, string? teacherName);
        Task<List<MediaCmsVideoDto>> GetVideosForCourseAsync(int courseId, string courseTitle, string courseCode, string? teacherName);
        Task<bool> AddVideoToPlaylistAsync(string playlistToken, string videoToken);
        Task<bool> RemoveVideoFromPlaylistAsync(string playlistToken, string videoToken);
        Task<MediaCmsCategoryDto?> GetOrCreateCourseCategoryAsync(int courseId, string courseTitle, string courseCode, string? description = null);
        Task<bool> AttachVideoToCategoryAsync(string videoToken, string categoryTitle);
        Task SyncAllCoursesAsync(IEnumerable<Course> courses);
    }
}
