using Microsoft.EntityFrameworkCore;
using MiniLms.Data;
using MiniLms.Interfaces;
using MiniLms.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MiniLms.Services
{
    public class CourseService : ICourseService
    {
        private readonly ApplicationDbContext _context;

        // Bağımlılık Enjeksiyonu (DI) ile DbContext servise bağlanıyor
        public CourseService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Tüm kursları sorumlu öğretmen bilgisiyle listeler.
        /// </summary>
        public async Task<IEnumerable<Course>> GetAllCoursesAsync()
        {
            return await _context.Courses
                .Include(c => c.Teacher)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <summary>
        /// Sadece belirli bir öğretmene ait veya henüz öğretmeni atanmamış genel kursları listeler.
        /// </summary>
        public async Task<IEnumerable<Course>> GetCoursesByTeacherIdAsync(string teacherId)
        {
            return await _context.Courses
                .Include(c => c.Teacher)
                .AsNoTracking()
                .Where(c => c.TeacherId == teacherId || c.TeacherId == null)
                .ToListAsync();
        }

        /// <summary>
        /// Belirli bir kursu; öğretmeni, dersleri, ders içerikleri ve dökümanlarıyla birlikte tam dolu getirir.
        /// </summary>
        public async Task<Course?> GetCourseByIdAsync(int id)
        {
            return await _context.Courses
                .Include(c => c.Teacher)
                .Include(c => c.Lessons)                  // 1. İlişki: Kursa ait haftalık dersleri yükler
                    .ThenInclude(l => l.Contents)         // 2. İlişki: Derslerin altındaki haftalık içerikleri yükler
                .Include(c => c.Documents)                // 🚀 3. İlişki: Arayüzde listelenmeyen dökümanları yükleyen kritik satır!
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        /// <summary>
        /// Yeni bir kurs oluşturur.
        /// </summary>
        public async Task AddCourseAsync(Course course)
        {
            await _context.Courses.AddAsync(course);
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Mevcut bir kursun bilgilerini günceller.
        /// </summary>
        public async Task UpdateCourseAsync(Course course)
        {
            _context.Courses.Update(course);
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Bir kursu sistemden tamamen siler.
        /// </summary>
        public async Task DeleteCourseAsync(int id)
        {
            var course = await _context.Courses.FindAsync(id);
            if (course != null)
            {
                _context.Courses.Remove(course);
                await _context.SaveChangesAsync();
            }
        }
    }
}