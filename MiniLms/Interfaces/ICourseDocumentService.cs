using Microsoft.AspNetCore.Http;
using MiniLms.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MiniLms.Interfaces
{
    public interface ICourseDocumentService
    {
        // Dosyayı sunucuya yükler ve DB kaydını atar
        Task SaveDocumentAsync(int courseId, IFormFile file);

        // Belirli bir derse ait dokümanları listeler
        Task<IEnumerable<CourseDocument>> GetDocumentsByCourseIdAsync(int courseId);

        // Dokümanı ID'sine göre getirir (Nullable '?' işareti servis ile birebir eşleşmeli)
        Task<CourseDocument?> GetDocumentByIdAsync(int id);

        // Dokümanı hem diskten hem DB'den siler
        Task DeleteDocumentAsync(int id);
        Task UploadDocumentAsync(int courseId, IFormFile file);
        Task<List<string>> GetDocumentTextChunksAsync(int documentId, int maxChunks = 5);
        Task EnsureDocumentTopicLessonsAsync(int courseId);
    }
}
