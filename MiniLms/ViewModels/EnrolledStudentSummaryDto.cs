using MiniLms.Models;
using System.Collections.Generic;

namespace MiniLms.ViewModels
{
    public class EnrolledStudentSummaryDto
    {
        public int StudentId { get; set; }
        public string? UserId { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string StudentNumber { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public List<DocumentSummary> Summaries { get; set; } = new();
    }
}
