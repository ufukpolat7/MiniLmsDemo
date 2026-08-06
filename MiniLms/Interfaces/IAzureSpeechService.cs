using System.Threading.Tasks;

namespace MiniLms.Interfaces
{
    public interface IAzureSpeechService
    {
        Task<string?> GenerateAudioSummaryAsync(string text, int documentId, string userId);
    }
}
