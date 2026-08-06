using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniLms.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSummaryTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Enrollments_StudentId",
                table: "Enrollments");

            migrationBuilder.CreateTable(
                name: "DocumentSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    CourseDocumentId = table.Column<int>(type: "int", nullable: false),
                    CourseId = table.Column<int>(type: "int", nullable: false),
                    SummaryText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentSummaries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DocumentSummaries_CourseDocuments_CourseDocumentId",
                        column: x => x.CourseDocumentId,
                        principalTable: "CourseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentSummaries_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Enrollments_StudentId_CourseId",
                table: "Enrollments",
                columns: new[] { "StudentId", "CourseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSummaries_CourseDocumentId",
                table: "DocumentSummaries",
                column: "CourseDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSummaries_CourseId",
                table: "DocumentSummaries",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSummaries_UserId",
                table: "DocumentSummaries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentSummaries");

            migrationBuilder.DropIndex(
                name: "IX_Enrollments_StudentId_CourseId",
                table: "Enrollments");

            migrationBuilder.CreateIndex(
                name: "IX_Enrollments_StudentId",
                table: "Enrollments",
                column: "StudentId");
        }
    }
}
